# Local test image for the HID transport contract tests. The .NET SDK is
# a development tool only: the shipped agent still targets C# 5 on the
# in-box .NET Framework compiler. Built as a separate layer over the
# approved base image so partial rebuilds do not reinstall the toolchain.
FROM titella-dev-local:latest

USER root
RUN apk add --no-cache dotnet8-sdk

USER dev
