# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

```bash
dotnet tool install -g bond-tools
bond breaking schema.bond --against .git#branch=main --error-format=json
bond format schema.bond
bond format schema.bond --check
```

## C# generation

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated \
  --descriptors --clone --equality --debugger --to-string
```

Requires `Bond.Runtime.CSharp`; the optional flags also require a matching
[`BondTools.Models`](Bond.Models/README.md) version.
See `bond generate csharp --help` for imports and mappings.

For automatic `.csproj` generation, use [`BondTools.Build`](Bond.Build/README.md).
