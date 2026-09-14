# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

## Quick Start

```bash
dotnet tool install -g bond-tools
```

Once installed, use the `bond` command:

```bash
bond breaking schema.bond --against .git#branch=main --error-format=json
bond breaking examples/catalog_v2.bond --against examples/catalog_v1.bond --error-format=json | jq .
bond breaking schema.bond --against .git#branch=main --ignore-imports
bond format schema.bond
bond format schema.bond --check
```

## Code Generation

```bash
bond generate csharp example.bond -o ./generated
```
