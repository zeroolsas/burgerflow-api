ARG DOTNET_VERSION=8.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
COPY --from=build /app/out ./
ENTRYPOINT ["dotnet", "BurgerFlow.dll"]
