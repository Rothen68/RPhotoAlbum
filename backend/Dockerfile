FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RPhotoAlbum.slnx ./
COPY src/RPhotoAlbum.Api/RPhotoAlbum.Api.csproj src/RPhotoAlbum.Api/
COPY tests/RPhotoAlbum.Api.Tests/RPhotoAlbum.Api.Tests.csproj tests/RPhotoAlbum.Api.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish src/RPhotoAlbum.Api/RPhotoAlbum.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "RPhotoAlbum.Api.dll"]
