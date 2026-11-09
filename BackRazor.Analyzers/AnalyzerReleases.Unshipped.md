; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
BRZ001 | BackRazor | Error | Marked classes must derive from ComponentBase
BRZ002 | BackRazor | Error | Owned disposable fields require a disposal contract
BRZ003 | BackRazor | Error | Browser and request dependencies are forbidden
BRZ004 | BackRazor | Error | Browser completion lifecycle is forbidden
BRZ005 | BackRazor | Error | Render code must not write state
BRZ006 | BackRazor | Error | Render calls must be verifiable
BRZ007 | BackRazor | Error | Non-constant static fields are forbidden
