# BondTools.Models

Optional model support for .NET 8+. Match the generator version:

```bash
dotnet add MyApp.csproj package BondTools.Models --version "$(bond --version)"
bond generate csharp order.bond -o Generated \
  --descriptors --clone --equality --debugger --to-string
```

```csharp
var schema = OrderSchema.Descriptor;
var copy = order.Clone();
bool same = order.Equals(copy);
Console.WriteLine(order); // Order { id = 42, ... }
```
