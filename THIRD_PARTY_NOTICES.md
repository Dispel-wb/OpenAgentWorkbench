# Third-party notices

Open Agent Workbench includes or interfaces with the following third-party components. Their original licenses and terms continue to apply.

## Bundled JavaScript libraries

- Vue.js 3.5.22 — MIT License — Copyright (c) 2018-present Yuxi (Evan) You and Vue contributors.
- xterm.js / @xterm packages 6.0.0 — MIT License — Copyright (c) 2017-2019 The xterm.js authors; Copyright (c) 2014-2017 SourceLair Private Company.

## Bundled .NET assemblies in release binaries

- Newtonsoft.Json — MIT License — Copyright (c) 2007 James Newton-King.
- Microsoft WebView2 SDK loader and assemblies — distributed under the Microsoft software license terms that accompany the WebView2 SDK. The WebView2 Runtime itself is not part of this source tree and is installed/updated by Microsoft.

## External products and services

The application can connect to user-selected model providers and can invoke compatible command-line agents, Skills and MCP servers. Those products are not redistributed by this repository and remain subject to their own terms.

The optional DSHarness runtime installer retrieves `@deepseek-ai/dsh` and its locked dependencies from npm. DSHarness is MIT-licensed; each transitive package retains its own license. The runtime is not embedded in the EXE or public source archive. Codex CLI and Claude Code CLI are separately installed products; their source, binaries and accounts are not included. See `runtimes/dsh/package-lock.json` for the exact optional dependency inventory.

Claude and Anthropic are trademarks of Anthropic PBC. OpenAI and Codex are trademarks of OpenAI. Microsoft, Windows, Office and WebView2 are trademarks of Microsoft. All other marks belong to their respective owners. Use of a name describes interoperability only and does not imply affiliation or endorsement.

No Claude, Codex or provider logo, mascot, screenshot, API credential, user workspace, transcript or private skin asset is included in the open-source release.
