# Crystal Code Apply Patch

**A Crystal Code external tool set that applies multi-file patches to the workspace.**

This is a native dotnet tool set for [Crystal Code](https://github.com/YELANDAOKONG/CrystalCode). `apply_patch` takes one patch text in the file-oriented format OpenCode uses — `*** Begin Patch` and `*** End Patch` around Add, Update, and Delete file sections — verifies the whole patch, then applies the sections in order inside the session workspace.

[Features](#features) · [Tools](#tools) · [Patch format](#patch-format) · [Paths and limits](#paths-and-limits) · [Install](#install) · [Build from source](#build-from-source)

## Features

- **One call, many files.** Add, Update, and Delete sections live in one patch text, so a multi-file refactor is one tool call.
- **Verify, then apply.** The patch is parsed and every Update is derived against the current file text before a single file changes. A parse or verification failure leaves every file untouched.
- **Sequential apply with honest failures.** Sections apply in order. If a later section fails because a file appeared, changed, or could not be written, the earlier sections stay applied and the failure names the failing path and lists what was applied.
- **OpenCode matching ladder.** Chunk lines match exactly first, then ignoring trailing whitespace, then trimmed, then ignoring Unicode quote, dash, and ellipsis differences; `*** End of File` anchors a chunk to the end of the file.
- **Workspace fence.** Paths resolve inside the session workspace. Absolute paths outside it, `..` escapes, symbolic links that leave it, and credential paths are refused.
- **Text safety.** Updates refuse files that look binary, keep a leading UTF-8 BOM, and end the result with a newline. Adds end with a newline unless they are empty.
- **Write-if-unchanged.** An Update is written only when the file still holds the exact bytes read during verification.
- **Bounded.** One Add section may create at most 2 MiB, matching the host write limit. The manifest gives the set 30 seconds per call.
- **Fits the host.** The set joins Work only and declares `approval: always`, so workspace-bounded patches pass automatically while the host still approves outside-workspace and forbidden classifications. `ApplyPatchTool` supplies its patch paths to the host before invocation through `CrystalCode.Tools.IHostToolApprovalClassifier`.

## Tools

| Tool | Catalog | What it does |
| :--- | :--- | :--- |
| `apply_patch` | Work | Apply one patch of Add, Update, and Delete sections in the workspace |

### apply_patch arguments

| Argument | Default | Meaning |
| :--- | :--- | :--- |
| `patchText` | Required | The full patch text describing Add, Update, and Delete file operations |

A call with an unknown property or a missing, empty, or non-string `patchText` fails with a plain-English message.

## Patch format

```text
*** Begin Patch
*** Add File: hello.txt
+Hello world
*** Update File: src/app.py
@@ def greet():
-print("Hi")
+print("Hello, world!")
*** Delete File: obsolete.txt
*** End Patch
```

- `*** Add File: <path>` creates one file. Every following line starts with `+` and contributes one line of the new contents; a section with no `+` lines creates an empty file.
- `*** Update File: <path>` patches an existing file in place. Each `@@` line starts a chunk; an optional text after `@@` is a change context that must be found before the chunk is applied. Context lines start with a space, removed lines with `-`, and added lines with `+`; context and removed lines must match the file text. A chunk that lists only `+` lines appends at the end of the file, and `*** End of File` inside a chunk anchors its match to the end of the file.
- `*** Delete File: <path>` removes one existing file. Nothing follows it.
- `*** Move to: <path>` can follow an Update header, but moves are rejected before any change with `apply_patch moves are not supported yet.`

The Begin and End markers may be surrounded by whitespace, the text may arrive wrapped in a `cat <<'EOF'` heredoc, and CRLF line endings are accepted. Sections apply in the order they appear. An Add may create parent directories; it refuses a path that already exists. An Update or Delete refuses a missing path and a directory.

### Matching

A chunk's context and removed lines are matched against the file in four passes, in order: exact equality, equality ignoring trailing whitespace, equality after trimming both sides, and equality after trimming and mapping Unicode curly quotes, dashes, ellipses, and no-break spaces to their ASCII forms. After a chunk is applied, later chunks in the same section continue after it.

### Text results

An Update result keeps the leading UTF-8 BOM the file had and always ends with a newline unless the file becomes empty. A file that does not end with a newline gains one. An Update whose file contains a null byte in its first 8192 bytes is refused as binary; Delete does not inspect file contents.

## Paths and limits

Paths in a patch are workspace-relative or absolute. Every resolved path must stay inside the session workspace, and resolution follows symbolic links to their final target, so a link that points outside the workspace is refused. `.ssh`, `.gnupg`, and `.crystal/credentials.json` paths are refused outright. The tool pre-classifies every patch path before invocation: workspace-bounded patches are Write + Workspace, outside paths are Write + OutsideWorkspace, and credential paths are Forbidden. With an effective author declaration, workspace-bounded calls pass automatically; outside paths still ask or go to Review/Audit, and forbidden paths never fully auto-pass. One Add section may create at most 2 MiB of contents (`2097152` bytes); larger additions fail before any file changes.

## Install

Publish the set and place the published files, including `tools.json`, in one directory under `~/.crystal/tools`:

```bash
dotnet publish CrystalCode.ApplyPatch/CrystalCode.ApplyPatch.csproj -c Release -o ~/.crystal/tools/patch
```

Restart the session, or run `/tools reload`, to load the set. A project set at `<workspace>/.crystal/tools/patch/` replaces the home set for that workspace. The directory name is the set identity; this set installs as `patch`.

## Build from source

You need the .NET 10 SDK and sibling checkouts of [Crystal](https://github.com/YELANDAOKONG/Crystal) at `../Crystal` and [Crystal Code](https://github.com/YELANDAOKONG/CrystalCode) at `../CrystalCode`. The set receives its workspace root through `CrystalCode.Tools.IHostTool`, so it references `CrystalCode.Tools` and `Crystal.Tools` as sibling source projects.

```bash
dotnet build CrystalCode.Tools.ApplyPatch.sln
dotnet test CrystalCode.Tools.ApplyPatch.sln
dotnet publish CrystalCode.ApplyPatch/CrystalCode.ApplyPatch.csproj
```

The production project adds no NuGet packages; the test project uses xUnit and Microsoft.NET.Test.Sdk only. `ApplyPatchTool` is the public `IHostTool` and `IHostToolApprovalClassifier` listed in `tools.json`; the host loads this class library in one isolated load context and wraps every call with its own approval, output truncation, and timeout. The patch language lives in `Patches`, and the workspace fence lives in `Workspace`, both covered by the test project.
