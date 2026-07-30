FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/DummyFileApi/DummyFileApi.csproj src/DummyFileApi/
RUN dotnet restore src/DummyFileApi/DummyFileApi.csproj

COPY src/DummyFileApi/ src/DummyFileApi/
RUN dotnet publish src/DummyFileApi/DummyFileApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

VOLUME /data
ENTRYPOINT ["dotnet", "DummyFileApi.dll"]
