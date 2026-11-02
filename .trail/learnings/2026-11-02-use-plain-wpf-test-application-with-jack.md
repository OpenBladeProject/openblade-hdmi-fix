---
title: Use plain WPF test Application with linked production resources
author: Jack
date: 2026-11-02
branch: fix/recording-preflight-status
touches:
  - VALIDATION.md
  - tests/OpenBlade.HdmiFix.Desktop.Tests/OpenBlade.HdmiFix.Desktop.Tests.csproj
  - tests/OpenBlade.HdmiFix.Desktop.Tests/RecordingPreflightWindowTests.cs
---

## Decided
Use a plain test-owned Application. Embed the production App.xaml source into the test project and load its Application.Resources children as a ResourceDictionary, retaining the actual window resources without constructing the production App.

## Tried and ruled out
This supersedes the draft learning about overriding App.OnStartup. A TestApplication subclass in the test assembly fails App.InitializeComponent's compiled BAML type/assembly check. Constructing the production App queues real OnStartup and opens a second window or an elevated-runner warning dialog. Removing Show alone and longer timeouts do not resolve that cause.

## Watch out
The startup isolation is test-only. Keep production administrator checks unchanged, actual Loaded/Record/Refresh handlers, exact control-state assertions, bounded dispatcher cleanup and separate displayed render checks. Linking App.xaml prevents test-only copies of production color values drifting.
