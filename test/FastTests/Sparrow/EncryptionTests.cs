﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FastTests.Voron;
using FastTests.Voron.FixedSize;
using Raven.Server.ServerWide;
using Sparrow;
using Voron;
using Voron.Data;
using Voron.Impl.Paging;
using Voron.Platform.Win32;
using Xunit;
using Voron.Global;
using Voron.Platform.Posix;
using Voron.Util.Settings;

namespace FastTests.Sparrow
{
    public class EncryptionTests : StorageTest
    {
        [Fact]
        public unsafe void WriteAndReadPageUsingCryptoPager()
        {
            using (var options = StorageEnvironmentOptions.ForPath(DataDir))
            {
                options.MasterKey = Sodium.GenerateMasterKey();

                using (var innerPager = GetPager(options))
                {
                    AbstractPager cryptoPager;
                    using (cryptoPager = new CryptoPager(innerPager))
                    {
                        using (var tx = new TempPagerTransaction(isWriteTransaction: true))
                        {
                            cryptoPager.EnsureContinuous(17, 1); // We're gonna try to read and write to page 17
                            var pagePointer = cryptoPager.AcquirePagePointerForNewPage(tx, 17, 1);

                            var header = (PageHeader*)pagePointer;
                            header->PageNumber = 17;
                            header->Flags = PageFlags.Single | PageFlags.FixedSizeTreePage;

                            Memory.Set(pagePointer + PageHeader.SizeOf, (byte)'X', Constants.Storage.PageSize - PageHeader.SizeOf);
                        }

                        using (var tx = new TempPagerTransaction())
                        {
                            var pagePointer = cryptoPager.AcquirePagePointer(tx, 17);

                            // Making sure that the data was decrypted and still holds those 'X' chars
                            Assert.True(pagePointer[PageHeader.SizeOf] == 'X');
                            Assert.True(pagePointer[666] == 'X');
                            Assert.True(pagePointer[1039] == 'X');
                        }
                    }
                }
            }
        }

        public static readonly bool RunningOnPosix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                                     RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private AbstractPager GetPager(StorageEnvironmentOptions options)
        {
            // tests on windows 64bits or linux 64bits only
            if (RunningOnPosix)
            {
                return new PosixMemoryMapPager(options, new VoronPathSetting(Path.Combine(DataDir, "Raven.Voron")));
            }
            return new WindowsMemoryMapPager(options, new VoronPathSetting(Path.Combine(DataDir, "Raven.Voron")));
        }

        [Theory]
        [InlineDataWithRandomSeed]
        public unsafe void WriteSeekAndReadInTempCryptoStream(int seed)
        {
            using (var options = StorageEnvironmentOptions.ForPath(DataDir))
            using (var stream = new TempCryptoStream(Path.Combine(DataDir, "EncryptedTempFile")))
            {
                var r = new Random(seed);

                var bytes = new byte[r.Next(128, 1024 * 1024 * 4)];
                fixed (byte* b = bytes)
                {
                    Memory.Set(b, (byte)'I', bytes.Length);
                }
                var injectionBytes = Encoding.UTF8.GetBytes("XXXXXXX");

                stream.Write(bytes, 0, bytes.Length);

                var someRandomLocationInTheMiddle = r.Next(0, bytes.Length - 7);
                fixed (byte* b = bytes)
                {
                    // injecting 7 'X' characters
                    Memory.Set(b + someRandomLocationInTheMiddle, (byte)'X', 7);
                }

                // Writing the same 7 'x's to the stream
                stream.Seek(someRandomLocationInTheMiddle, SeekOrigin.Begin);
                stream.Write(injectionBytes, 0, injectionBytes.Length);

                // Reading the entire stream back.
                var readBytes = new byte[bytes.Length];
                stream.Seek(0, SeekOrigin.Begin);

                var count = readBytes.Length;
                var offset = 0;
                while (count > 0)
                {
                    var read = stream.Read(readBytes, offset, count);
                    count -= read;
                    offset += read;
                }
                Assert.Equal(bytes, readBytes);
            }
        }
    }
}
