# MixFrame

MixFrame is a Windows local batch media conversion tool built with WinUI 3 and C#.

It supports two workspaces:

- Image conversion: PNG, JPG/JPEG, and static WebP to WebP, JPG, or PNG.
- Video conversion: common video files and animated WebP to animated WebP, MP4, GIF, or WebM.

The app is designed for local processing, batch import/export, per-file settings, presets, and website-ready output.

## Requirements

- Windows 10 or Windows 11 x64
- .NET 10 SDK for development
- Windows App SDK 1.8
- FFmpeg / ffprobe 8.1.2 essentials build for media conversion

## Development

Restore and build:

```powershell
dotnet restore MixFrame\MixFrame.csproj -p:Platform=x64 -p:NuGetAudit=false
dotnet build MixFrame\MixFrame.csproj -c Debug -p:Platform=x64 --no-restore
```

## Release Package

Create the portable Windows x64 package:

```powershell
.\Publish-WinX64.ps1
```

The script expects a local `ffmpeg-8.1.2-essentials_build` folder beside the project. The FFmpeg bundle is not committed to this source repository; include it only in the published zip package and keep its license files with the release.

Current portable output:

```text
dist\MixFrame-win-x64.zip
```

## License

No open-source license has been selected yet. Until a license is added, all rights are reserved by default.
