# run

A convenient place to keep a built `SuperCommander.exe` so you can launch it
without digging through `bin\`.

The executable itself is **not** committed — a 57 MB binary would bloat the
repository permanently, and released builds are already attached to each
[GitHub release](https://github.com/robertorenz/superCommander/releases).

To fill this folder, run from the repository root:

```
publish.cmd
```

That produces the self-contained single file, which runs on a machine with no
.NET installed. For the small build instead (needs the
[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)):

```
publish.cmd fd
```
