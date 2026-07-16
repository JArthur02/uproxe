# AGENTS.md

## Cursor Cloud specific instructions

This repository is **not a buildable software project**. It is a flat bundle of
precompiled Windows binaries plus config/data files, with **no source code**.

Contents (`git ls-files`):

- Windows executables (Windows-only): `Proxifier.exe`, `ProxyChecker.exe`,
  `ServiceManager.exe`, `tool.exe`
- Windows kernel-mode driver: `ProxifierDrv.sys` (+ `ProxifierDrv.inf`, `ProxifierDrv.cat`)
- .NET assembly dependency: `Ionic.Zip.dll`
- MaxMind GeoIP database: `Country.mmdb`
- Config / data / misc text: `check.ini`, `gate.txt`, `assembly.txt`, `all.js`, `LICENSE`

There is **no development environment to set up**, because there is:

- no source code to compile;
- no package manager or dependency manifest (no `package.json`, `requirements.txt`,
  `*.csproj`/`*.sln`, `go.mod`, `Cargo.toml`, etc.);
- no build system (no `Makefile`, MSBuild project, etc.);
- no test suite, lint config, README, Dockerfile, or devcontainer.

Consequently there are **no lint, test, build, or dev-run commands** for this repo,
and the update script is intentionally a no-op.

Platform note: the Cursor Cloud VM is **Linux x86_64 with no Wine/Mono**. Every
executable here is a Windows PE binary (one is a kernel-mode WFP driver requiring
Administrator rights and driver-signing trust), so **none of these programs can run
on this host**. Running/testing these tools requires a Windows machine.

If real source code is added later, replace this section with the actual
install/lint/test/build/run instructions for that code.
