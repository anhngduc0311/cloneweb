FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
RUN mkdir -p /app/wwwroot/uploads && chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 5080
ENTRYPOINT ["dotnet", "TruyenDex.Api.dll"]
