FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY application/ /app
RUN chmod -R a+rX /app
USER 65532:65532
ENTRYPOINT ["dotnet", "IsolatedMcpServer.dll"]
