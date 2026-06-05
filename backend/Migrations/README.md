# Migrations

The `AddAuthTables` migration needs to be generated after the first Docker build.

Run inside the API container or on a machine with NuGet access to Fido2NetLib >= 3.0.0:

```bash
dotnet ef migrations add AddAuthTables
```

All previous migrations (InitialSchema, AddInstitutionEntity, RemovePayeeText, AddFieldLengthLimits)
were generated and are ready to apply.
