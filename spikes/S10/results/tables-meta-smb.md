
**normal**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) | FAILED: 87 (The parameter is incorrect.) | | | | | | | |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | kept (2001) | TEMP's | 1->1 |
| ReplaceFileW + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 1->1 |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | FAILED: 87 (The parameter is incorrect.) | | | | | | | |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | FAILED: 87 (The parameter is incorrect.) | | | | | | | |

**hardlink**

| mechanism | result | DACL | Zone.Identifier | other stream | attributes | creation time | file ID | links |
|---|---|---|---|---|---|---|---|---|
| ReplaceFileW | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS) | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| MoveFileEx(REPLACE_EXISTING) | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | TEMP's (new) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| RenameEx(REPLACE|POSIX) | FAILED: 87 (The parameter is incorrect.) | | | | | | | |
| swap: rename target away, rename temp in | ok | TEMP's (original lost) | LOST | LOST | 00002022->00000022 CHANGED | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| ReplaceFileW + explicit metadata copy first | ok | kept | kept | kept | 00002022->00002022 kept | kept (2001) | TEMP's | 2->1 ; other hard link: still OLD content (link broken) |
| RenameEx(REPLACE|POSIX) + explicit metadata copy first | FAILED: 87 (The parameter is incorrect.) | | | | | | | |
| RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved | FAILED: 87 (The parameter is incorrect.) | | | | | | | |
