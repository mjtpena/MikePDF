
**normal**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |

**readonly-attr**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | FAILED: 5 (Access is denied.) | | | | | | | |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | FAILED: 5 (Access is denied.) | | | | | | | |
| MoveFileEx(REPLACE_EXISTING) | FAILED: 5 (Access is denied.) | | | | | | | |
| RenameEx(REPLACE|POSIX) | FAILED: 5 (Access is denied.) | | | | | | | |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002023->00000022 CHANGED | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW + explicit metadata copy first | FAILED: 5 (Access is denied.) | | | | | | | |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | FAILED: 5 (Access is denied.) | | | | | | | |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | FAILED: 5 (Access is denied.) | | | | | | | |

**compressed**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | ok | kept | kept | kept | 00002822->00002822 kept | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | ok | kept | kept | kept | 00002822->00002822 kept | kept (2001) | TEMP's | 1->1 |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002822->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) | ok | TEMP's (original lost) | LOST | LOST | 00002822->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002822->00000022 CHANGED | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW + explicit metadata copy first | ok | kept | kept | kept | 00002822->00002822 kept | kept (2001) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | ok | kept | kept | kept | 00002822->00002022 CHANGED | kept (2001) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | ok | kept | kept | kept | 00002822->00002022 CHANGED | kept (2001) | TEMP's | 1->1 |

**symlink**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | FAILED: 1464 (This application does not support the current operation on symbolic links.) | | | | | | | |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | FAILED: 1464 (This application does not support the current operation on symbolic links.) | | | | | | | |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 ; symlink: REPLACED BY REGULAR FILE; real file: real file untouched |
| RenameEx(REPLACE|POSIX) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 ; symlink: REPLACED BY REGULAR FILE; real file: real file untouched |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 ; symlink: REPLACED BY REGULAR FILE; real file: real file untouched |
| ReplaceFileW + explicit metadata copy first | FAILED: 1464 (This application does not support the current operation on symbolic links.) | | | | | | | |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 ; symlink: REPLACED BY REGULAR FILE; real file: real file untouched |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 ; symlink: still a symlink; real file: real file updated |

**hardlink**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| RenameEx(REPLACE|POSIX) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| ReplaceFileW + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
