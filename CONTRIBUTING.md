# Contributing to ByteExplorer

Thank you for wanting to help! Bug reports, ideas and pull requests are all welcome.

## Reporting a bug or asking for a feature

Open an [issue](../../issues/new/choose) and pick **Bug report** or **Feature request**. For a bug, the most useful
things are: what you did, what you expected, what happened instead, your system (Windows, Linux distribution, macOS)
and, if it crashed, the log file from **Settings › About › Logs**.

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (a newer SDK that can target .NET 9 works
too).

```bash
dotnet build ByteExplorer.sln
```

```bash
dotnet run --project ByteExplorer.csproj
```

## Testing

The tests use xUnit and Avalonia's headless platform, so they run without a screen, on any system:

```bash
dotnet test ByteExplorer.sln
```

A full run takes a few minutes. Please run it before sending a pull request.

## Sending a change

1. Fork the repository and create a branch for your change.
2. Keep the change focused on one thing, and match the style of the code around it (explicit usings, XML doc comments
   that explain *why*, no new warnings: the build is warning-free).
3. Add or update tests for what you changed.
4. New source files start with the MPL 2.0 notice used by every file in the project:
   ```csharp
   // This Source Code Form is subject to the terms of the Mozilla Public
   // License, v. 2.0. If a copy of the MPL was not distributed with this
   // file, You can obtain one at https://mozilla.org/MPL/2.0/.
   ```
5. Open a pull request describing what it does and how you tested it.

## Ground rules of the project

- **No network access by the app on its own**, no telemetry, no automatic updates. A feature may open a link or a
  network location only when the user asks for it.
- **New NuGet packages are added only with a good reason**, and every component that ships with the app must be listed
  with its license in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **File operations must stay safe:** never leave a half-written file, never follow links when deleting, and keep
  operations undoable where they are today.

## License of contributions

ByteExplorer is licensed under the [Mozilla Public License 2.0](LICENSE). By sending a pull request, you agree that
your contribution is licensed under the same license.
