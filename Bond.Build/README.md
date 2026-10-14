# BondTools.Build

Generates C# for `Bond` items during `dotnet build` (.NET 8+ SDK).

```xml
<PropertyGroup>
  <BondClone>true</BondClone>
  <BondEquality>true</BondEquality>
  <BondToString>true</BondToString>
  <BondClear>true</BondClear>
  <BondSerialization>true</BondSerialization>
  <BondImportDirectories>Schemas/Shared</BondImportDirectories>
  <BondUsings>System.Collections.Generic</BondUsings>
  <BondNamespaceMappings>Contracts=MyApp.Contracts</BondNamespaceMappings>
  <BondTypeMappings>Contracts.Timestamp=System.DateTime</BondTypeMappings>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="BondTools.Build" Version="VERSION" PrivateAssets="all" />
  <PackageReference Include="Bond.Runtime.CSharp" Version="13.0.2" />
  <Bond Include="Schemas/**/*.bond" />
</ItemGroup>
```

Lists are separated by `;`. Generated files live in the intermediate output directory.
