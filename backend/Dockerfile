FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/DBSync.API/DBSync.API.csproj -c Release -o /out/api \
 && dotnet publish src/DBSync.Worker/DBSync.Worker.csproj -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DBSync.API.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENTRYPOINT ["dotnet", "DBSync.Worker.dll"]
