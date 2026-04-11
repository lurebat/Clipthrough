# Contributing

## Setup

1. Install the .NET 10 SDK.
2. Clone the repository.
3. Restore dependencies:

```powershell
dotnet restore .\Clipthrough.slnx
```

## Before opening a pull request

1. Run the test suite:

```powershell
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj
```

2. If you changed application startup, views, or packaging, also run:

```powershell
dotnet build .\Clipthrough\Clipthrough.csproj
```

3. Keep commits focused and describe the user-visible or architectural change clearly.

## Pull request guidance

- Explain the problem and the solution.
- Mention any schema, workflow, or settings changes.
- Include screenshots when the UI changes materially.
