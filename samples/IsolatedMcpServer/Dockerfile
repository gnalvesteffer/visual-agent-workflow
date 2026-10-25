FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:ecf8dcbaab8ebc4bdfb2322d6fe4217bbb118894310ebb6d53c580874ab4b27d
WORKDIR /app
COPY application/ /app
RUN chmod -R a+w /app
USER 65532:65532
ENTRYPOINT ["dotnet", "IsolatedMcpServer.dll"]
