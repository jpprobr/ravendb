#if defined(__unix__) || defined(__APPLE__)

#define _GNU_SOURCE
#include <unistd.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <errno.h>
#include <stdlib.h>
#include <assert.h>
#include <string.h>
#include <libgen.h>

#include "rvn.h"
#include "internal_posix.h"
#include "status_codes.h"



static
int32_t
sync_directory_for_internal (char *dir_path, uint32_t * detailed_error_code) {
  int rc;
  int fd = open (dir_path, 0, 0);
  if (fd == -1) {
      rc = FAIL_OPEN_FILE;
      goto error_cleanup;
  }

  rc = sync_directory_allowed (fd);

  if (rc == SYNC_DIR_FAILED){
      goto error_cleanup;
  }

  if (rc == SYNC_DIR_NOT_ALLOWED) {
      rc = SUCCESS;
      goto error_cleanup;
  }

  if (flush_file (fd) == -1) {
      rc = FAIL_FLUSH_FILE;
      goto error_cleanup;
  }

  rc = SUCCESS;

error_cleanup:
  *detailed_error_code = errno;
  if (fd != -1)
    close (fd);

  return rc;
}

static
int32_t
sync_directory_maybe_symblink (char *dir_path, int depth,
			       uint32_t * detailed_error_code) {
  struct stat sb;
  char *link_name = NULL;
  int rc;

  int steps = 10;

  while (1) {
      if (lstat (dir_path, &sb) == -1) {
    	  rc = FAIL_STAT_FILE;
    	  goto error_cleanup;
    	}

      link_name = malloc (sb.st_size + 1);
      if (link_name == NULL) {
    	  rc = FAIL_NOMEM;
    	  goto error_cleanup;
    	}

      int len = readlink (dir_path, link_name, sb.st_size + 1);

      if (len == 0 || (len == -1 && errno == EINVAL)){	/* EINVAL on non-symlink dir_path */ 
    	  rc = sync_directory_for_internal (dir_path, detailed_error_code);
    	  goto success;
    	}

      if (len < 0) {
    	  rc = FAIL_STAT_FILE;
    	  goto error_cleanup;
    	}

      if (len > sb.st_size) {
    	  /* race: the link has changed, re-read */
    	  free (link_name);
    	  link_name = NULL;
    	  if (steps-- > 0)
    	    continue;
    	  rc = FAIL_RACE_RETRIES;
    	  goto error_cleanup;
    	}

      link_name[len] = '\0';
      break;
  }

  if (depth == 0) {
    rc = FAIL_PATH_RECURSION;
    goto error_cleanup;
  }

  rc = sync_directory_maybe_symblink (link_name, 
        depth - 1, 
        detailed_error_code);

error_cleanup:
  *detailed_error_code = errno;
success:
  if (link_name != NULL)
    free (link_name);

  return rc;
}

EXPORT int32_t
sync_directory_for (const char *file_path, uint32_t * detailed_error_code) {
  assert (file_path != NULL);

  char *file_path_copy = NULL;
  file_path_copy = strdup (file_path);
  if (file_path_copy == NULL) {
    *detailed_error_code = errno;
    return FAIL_NOMEM;
  }
  char *dir_path = dirname (file_path_copy);

  int32_t rc = sync_directory_maybe_symblink (dir_path, 
    256, /* even that is probably just abuse */
    detailed_error_code);

  free (file_path_copy);

  return rc;
}

#endif