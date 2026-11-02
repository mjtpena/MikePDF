| broker share | engine view | open-for-write | open-for-append | touch-mtime (attribute-only open) | rename | delete | posix-delete | ReplaceFileW over it | open-for-read |
|---|---|---|---|---|---|---|---|---|---|
| R (ADR-0005 as built) | no | denied (32) | denied (32) | allowed | denied (32) | denied (32) | denied (32) | denied (32) | allowed |
| R (ADR-0005 as built) | yes | denied (32) | denied (32) | allowed | denied (32) | denied (32) | denied (32) | denied (32) | allowed |
| R|D (proposed) | no | denied (32) | denied (32) | allowed | allowed | allowed | allowed | allowed | allowed |
| R|D (proposed) | yes | denied (32) | denied (32) | allowed | allowed | allowed | allowed | allowed | allowed |
| R|W|D | no | allowed | allowed | allowed | allowed | allowed | allowed | allowed | allowed |
| R|W|D | yes | allowed | allowed | allowed | allowed | allowed | allowed | allowed | allowed |
| none | no | denied (32) | denied (32) | allowed | denied (32) | denied (32) | denied (32) | denied (32) | denied (32) |
| none | yes | denied (32) | denied (32) | allowed | denied (32) | denied (32) | denied (32) | denied (32) | denied (32) |
