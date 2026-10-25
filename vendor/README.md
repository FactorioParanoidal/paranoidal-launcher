# vendor

- `MinGit-2.56.0.2-64-bit.zip` — MinGit (portable git) from Git for Windows, unchanged.
  Source and release: https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2
  SHA-256 `da35e72aa21c005a5a0d298cfbae110bc1609a815730ea0dde84b01a1b3cd3be` (checked by `build.ps1`).
- `MinGit-LICENSE.txt` — its licence (GNU GPL v2 and the licences of the bundled components), copied from the zip.

The launcher embeds this zip as a resource and unpacks it into its data folder on the first start. Git for Windows
sources: https://github.com/git-for-windows/git.
