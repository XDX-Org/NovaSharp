# Preview known limitations

- Intel macOS uses netcoredbg 3.1.3 because upstream 3.2 no longer publishes an Intel archive; capability negotiation hides version differences.
- On macOS, the packaged netcoredbg adapters launch and pause managed targets but leave source breakpoints pending in clean runners. Phase 12 remains incomplete until that binding path is fixed or replaced.
- Debugger views, extension-process isolation, Build Configurator UI, signing/notarization, installers, and atomic updates are still under implementation.
- Native interaction automation runs under Linux/Xvfb. Windows and macOS receive build, test, and package checks but still need retained clean-image UI evidence for release qualification.
- Legacy/non-SDK projects, JavaScript/TypeScript intelligence, source-control UI, and remote development are outside preview scope.
- Uninstall retains settings and recovery data by design.
