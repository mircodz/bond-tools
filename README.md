# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

```bash
dotnet tool install -g bond-tools
bond check schema.bond
bond breaking schema.bond --against .git#branch=main --error-format=json
bond format schema.bond
bond format schema.bond --check
```

## C# generation

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated \
  --descriptors --clone --equality --debugger --to-string
```
