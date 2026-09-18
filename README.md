# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

## Installation

```bash
dotnet tool install -g bond-tools
```

## Quick Start

```
bond breaking schema.bond --against .git#branch=main --error-format=json
bond format schema.bond
bond format schema.bond --check
```

## C# generation

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated
```

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated \
  --descriptors --clone --equality --debugger
```

Schema comments become XML documentation on generated types, properties, and enum members.

### MSBuild

Reference `BondTools.Build` with `PrivateAssets="all"` and `Bond.Runtime.CSharp`, then add:

```xml
<ItemGroup>
  <Bond Include="Schemas/**/*.bond" />
</ItemGroup>
```

`dotnet build` generates C# under `obj/` (`out/obj/` in this repository), tracks
import changes, and includes the output automatically. No global tool is needed.
Set `Descriptors`, `Clone`, `Equality`, or `Debugger` metadata to `true` for
individual schemas and reference the matching `BondTools.Models` package.

### Mappings

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated \
  --namespace Contracts=MyApp.Contracts \
  --type-map Contracts.Timestamp=System.DateTime
```

Add C# imports with repeatable `-u`/`--using` options, e.g. `-u System --using System.Collections.Generic`.
