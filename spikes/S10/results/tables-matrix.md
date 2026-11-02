| holder | ReplaceFileW | ReplaceFileW+backup | MoveFileEx(REPLACE_EXISTING) | RenameEx(REPLACE|POSIX) | RenameEx(REPLACE) | RenameInfo(ReplaceIfExists) | swap: rename target away, rename temp in | delete target (POSIX), rename temp in |
|---|---|---|---|---|---|---|---|---|
| none | OK | OK | OK | OK | OK | OK | OK | OK |
| broker-handle R (ADR-0005 flags, no view) | fail 32 | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker-handle R|D (no view) | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |
| broker R + engine view (ADR-0005 as built) | fail 32 | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker R|D + engine view | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |
| broker R|W|D + engine view | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |
| engine view only, broker closed (opened R) | OK | OK | OK | OK | OK | OK | OK | OK |
| engine view only, broker closed (opened R|D) | OK | OK | OK | OK | OK | OK | OK | OK |
| broker R + view in ENGINE PROCESS | fail 32 | fail 32 | fail 5 | fail 32 | fail 5 | fail 5 | fail 32 | fail 32 |
| broker R|D + view in ENGINE PROCESS | OK | OK | fail 5 | OK | fail 5 | fail 5 | OK | OK |

holders that did not see the old content intact after a successful replace: 0
