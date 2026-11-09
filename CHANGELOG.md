# Changelog

## 0.3.0-preview.1

First public preview.

- Process-owned Razor roots and DI scopes operate without browser observers.
- Immutable keyed configuration transitions retain equal objects and wait for asynchronous cleanup before replacement; cleanup failures block further creation.
- Components rerender independently through the Blazor Dispatcher.
- Read-only change notifications and Dispatcher-scoped render access; generic Observer readers and ChildContent choose conversion and display.
- Opt-in component diagnostics report against Razor source locations.
- Sparkplug B example, package consumer checks, lifetime tests and browser/MQTT integration tests.
- Targets .NET 11 RC3. NativeAOT remains experimental. APIs may change during preview.

Licensed under VibeCoded AI-Slop License v1.0. External dependencies retain their licenses.
