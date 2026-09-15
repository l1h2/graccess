# Setup tools

Tools that prepare this machine for the other tools, such as saving galaxy credentials.

Rules:

- They may log in to test a connection, but never modify a galaxy.
- Anything secret goes to Windows Credential Manager, never to files under the project.
- Name tools `Save...` or `Configure...`.
