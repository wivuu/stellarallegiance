## dotnet restore hangs

If `dotnet restore` hangs/takes forever, stopped (`T` state) `aspire-managed` processes are likely holding a NuGet scratch lock file in `/private/var/folders/.../NuGetScratch/lock/`. Kill them:

```sh
ps aux | grep "[a]spire-managed" | awk '{print $2}' | xargs kill -9
# Then clean up stale locks
rm -f /private/var/folders/*/*/*/NuGetScratch/lock/*
```
