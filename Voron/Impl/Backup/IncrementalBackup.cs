﻿// -----------------------------------------------------------------------
//  <copyright file="IncrementalBackup.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Voron.Impl.Journal;
using Voron.Trees;
using Voron.Util;

namespace Voron.Impl.Backup
{
    public unsafe class IncrementalBackup
    {
		public long ToFile(StorageEnvironment env, string backupPath, CompressionLevel compression = CompressionLevel.Optimal)
        {
            if (env.Options.IncrementalBackupEnabled == false)
                throw new InvalidOperationException("Incremental backup is disabled for this storage");

            long numberOfBackedUpPages = 0;

            var copier = new DataCopier(env.PageSize * 16);
            var backupSuccess = true;

            IncrementalBackupInfo backupInfo;
            long lastWrittenLogPage = -1;
            long lastWrittenLogFile = -1;

            using (var txw = env.NewTransaction(TransactionFlags.ReadWrite))
            {
                backupInfo = env.Journal.GetIncrementalBackupInfo();

                if (env.Journal.CurrentFile != null)
                {
                    lastWrittenLogFile = env.Journal.CurrentFile.Number;
                    lastWrittenLogPage = env.Journal.CurrentFile.WritePagePosition;
                }

                // txw.Commit(); intentionally not committing
            }

            using (env.NewTransaction(TransactionFlags.Read))
            {
                var usedJournals = new List<JournalFile>();

                try
                {
					using(var file = new FileStream(backupPath, FileMode.Create))
                    using (var package = new ZipArchive(file, ZipArchiveMode.Create))
                    {
                        long lastBackedUpPage = -1;
                        long lastBackedUpFile = -1;

                        var firstJournalToBackup = backupInfo.LastBackedUpJournal;

                        if (firstJournalToBackup == -1)
                            firstJournalToBackup = 0; // first time that we do incremental backup

                        for (var journalNum = firstJournalToBackup; journalNum <= backupInfo.LastCreatedJournal; journalNum++)
                        {
                            var journalFile = env.Journal.Files.Find(x => x.Number == journalNum); // first check journal files currently being in use
                            if (journalFile == null)
                            {
                                journalFile = new JournalFile(env.Options.CreateJournalPager(journalNum), journalNum);
                            }

                            journalFile.AddRef();

                            usedJournals.Add(journalFile);

                            var startBackupAt = 0L;
                            var pagesToCopy = journalFile.Pager.NumberOfAllocatedPages;
                            if (journalFile.Number == backupInfo.LastBackedUpJournal)
                            {
                                startBackupAt = backupInfo.LastBackedUpJournalPage + 1;
                                pagesToCopy -= startBackupAt;
                            }

                            if (startBackupAt >= journalFile.Pager.NumberOfAllocatedPages) // nothing to do here
                                continue;

                            var part = package.CreateEntry(StorageEnvironmentOptions.JournalName(journalNum), compression);
                            Debug.Assert(part != null);

                            if (journalFile.Number == lastWrittenLogFile)
                                pagesToCopy -= (journalFile.Pager.NumberOfAllocatedPages - lastWrittenLogPage);

                            using(var stream = part.Open())
	                        {
		                        copier.ToStream(journalFile.Pager.Read(startBackupAt).Base, pagesToCopy * journalFile.Pager.PageSize, stream);
	                        }

                            lastBackedUpFile = journalFile.Number;
                            if (journalFile.Number == backupInfo.LastCreatedJournal)
                            {
                                lastBackedUpPage = startBackupAt + pagesToCopy - 1;
                                // we used all of this file, so the next backup should start in the next file
                                if (lastBackedUpPage == journalFile.Pager.NumberOfAllocatedPages)
                                {
                                    lastBackedUpPage = 0;
                                    lastBackedUpFile++;
                                }
                            }

                            numberOfBackedUpPages += pagesToCopy;
                        }

                        Debug.Assert(lastBackedUpPage != -1);

                        env.Journal.UpdateAfterIncrementalBackup(lastBackedUpFile, lastBackedUpPage);
                    }
                }
                catch (Exception)
                {
                    backupSuccess = false;
                    throw;
                }
                finally
                {
                    foreach (var file in usedJournals)
                    {
                        if (backupSuccess) // if backup succeeded we can remove journals
                        {
                            if (file.Number != lastWrittenLogFile) // prevent deletion of the current journal
                            {
                                file.DeleteOnClose();
                            }
                        }

                        file.Release();
                    }
                }

                return numberOfBackedUpPages;
            }
        }

        public void Restore(StorageEnvironment env, string backupPath)
        {
            using (var txw = env.NewTransaction(TransactionFlags.ReadWrite))
            {
                using (env.Options.AllowManualFlushing())
                {
                    env.FlushLogToDataFile();
                }

                List<string> journalNames;

				using (var package = ZipFile.Open(backupPath, ZipArchiveMode.Read))
                {
                    journalNames = package.Entries.Select(x => x.Name).ToList();
                }

                var tempDir = Directory.CreateDirectory(Path.GetTempPath() + Guid.NewGuid()).FullName;
                var toDispose = new List<IDisposable>();

                try
                {
                    ZipFile.ExtractToDirectory(backupPath, tempDir);

                    TransactionHeader* lastTxHeader = null;

                    var pagesToWrite = new Dictionary<long, Func<Page>>();

                    foreach (var journalName in journalNames)
                    {
                        var pager = new MemoryMapPager(Path.Combine(tempDir, journalName));
                        toDispose.Add(pager);

                        long number;

                        if (long.TryParse(journalName.Replace(".journal", string.Empty), out number) == false)
                        {
                            throw new InvalidOperationException("Cannot parse journal file number");
                        }

                        var reader = new JournalReader(pager, 0, lastTxHeader);

                        while (reader.ReadOneTransaction())
                        {
                            lastTxHeader = reader.LastTransactionHeader;
                        }

                        foreach (var translation in reader.TransactionPageTranslation)
                        {
                            var pageInJournal = translation.Value;
                            pagesToWrite[translation.Key] = () => pager.Read(pageInJournal);
                        }
                    }

                    var sortedPages = pagesToWrite.OrderBy(x => x.Key)
                                                  .Select(x => x.Value())
                                                  .ToList();

                    var last = sortedPages.Last();

                    env.Options.DataPager.EnsureContinuous(null, last.PageNumber,
                                                    last.IsOverflow
                                                        ? env.Options.DataPager.GetNumberOfOverflowPages(
                                                            last.OverflowSize)
                                                        : 1);

                    foreach (var page in sortedPages)
                    {
                        env.Options.DataPager.Write(page);
                    }

                    env.Options.DataPager.Sync();

                    txw.State.Root = Tree.Open(txw, env._sliceComparer, &lastTxHeader->Root);
                    txw.State.FreeSpaceRoot = Tree.Open(txw, env._sliceComparer, &lastTxHeader->FreeSpace);

                    txw.State.FreeSpaceRoot.Name = Constants.FreeSpaceTreeName;
                    txw.State.Root.Name = Constants.RootTreeName;

                    txw.State.NextPageNumber = lastTxHeader->LastPageNumber + 1;

                    txw.Commit();

                    env.Journal.Clear();
                }
                finally
                {
                    toDispose.ForEach(x => x.Dispose());

                    Directory.Delete(tempDir, true);
                }

                // txw.Commit(); no need to commit that
            }
        }
    }
}