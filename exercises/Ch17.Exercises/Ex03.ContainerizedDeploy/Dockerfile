# ============================================================================
# Exercise 03 — the image, built from a published folder.
#
# The default path in the chapter needs no Dockerfile at all:
#   dotnet publish -c Release --os linux --arch x64 /t:PublishContainer
#
# This file is for teams that keep one in source control. It copies the output
# of `dotnet publish` (the build context), so building the image needs no SDK
# image and no network:
#   dotnet publish exercises/Ch17.Exercises/Ex03.ContainerizedDeploy -c Release -o <dir>
#   docker build --pull=false --network none -f exercises/Ch17.Exercises/Ex03.ContainerizedDeploy/Dockerfile -t ch17-ex03-agent-api <dir>
# check-image.ps1 runs those two steps and checks the result.
# ============================================================================

# Chiseled: no shell, no package manager, a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY . .

# No ENV or ARG carries a key: the provider key and the token settings arrive at
# run time (-e, a mounted secret or a managed identity), never in a layer.
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ex03.ContainerizedDeploy.dll"]
