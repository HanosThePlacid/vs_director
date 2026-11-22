# Contributing to VS Director

## Pull requests

Branch from `main` and target `main` when opening your pull request. Keep a feature and the changes it depends on in one PR so reviewers can try them together. Follow the naming, formatting, and file layout of the surrounding code.

Describe what changes for users, how you tested it, and the results. For changes to replay or timeline logic, run the core tests from the repository root:

```sh
dotnet run --project tests/VSDirector.CoreTests/VSDirector.CoreTests.csproj -c Release
```

For changes to the game integration, see the compatibility checks in the [README](README.md). For UI changes, include the in-game steps a reviewer can use to check the behavior.

## Localization

Put all new user-facing text in the [English language file](assets/vsdirector/lang/en.json). This includes preset names, descriptions, and error messages. Use stable translation keys in code and look them up with `Lang.Get`. Keep the English wording in the language file only. The maintainer handles the Simplified Chinese translation.

## Saved data

Replay documents and standalone timeline edit files have separate schema versions. When changing a saved field or track type, check every format that stores it. Choose defaults that preserve playback for older files. If an older reader could silently discard data when saving, bump the schema version of each affected format so that reader can reject it.

Test both directions of interest: load an older file and check its behavior, then save and reload a file that uses the new feature and check that its data survives. Describe any intentional change to existing playback in the PR.

## Voice integration

Voice mods and external adapters should submit decoded audio through the public [DirectorVoiceApi](src/Voice/DirectorVoiceApi.cs). Keep mod-specific integration code in the voice mod or its external adapter. Features that use speech as an optional signal should also work with replays that contain no voice data.
