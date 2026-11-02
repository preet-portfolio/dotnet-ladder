# Mutation Testing Summary

| File                        | Score  | Killed | Survived | Timeout | No Coverage | Ignored | Compile Errors | Runtime Errors | Total Detected | Total Undetected | Total Mutants |
| --------------------------- | ------ | ------ | -------- | ------- | ----------- | ------- | -------------- | -------------- | -------------- | ---------------- | ------------- |
| CommandArgs.cs              | 72.00% | 17     | 3        | 1       | 4           | 7       | 1              | 0              | 18             | 7                | 33            |
| Commands\/CommandContext.cs | 60.00% | 9      | 4        | 0       | 2           | 5       | 2              | 0              | 9              | 6                | 22            |
| Commands\/CommandTable.cs   | 85.71% | 48     | 2        | 0       | 6           | 12      | 10             | 0              | 48             | 8                | 78            |
| Commands\/KeyCommands.cs    | 43.66% | 203    | 65       | 0       | 197         | 88      | 17             | 0              | 203            | 262              | 570           |
| Commands\/ServerCommands.cs | 8.79%  | 29     | 65       | 0       | 236         | 60      | 52             | 0              | 29             | 301              | 442           |
| Commands\/StringCommands.cs | 67.66% | 520    | 83       | 1       | 166         | 130     | 126            | 0              | 521            | 249              | 1026          |
| Commands\/TypeCommands.cs   | 9.58%  | 16     | 37       | 0       | 114         | 53      | 112            | 0              | 16             | 151              | 332           |
| RespParser.cs               | 91.49% | 84     | 7        | 2       | 1           | 16      | 1              | 0              | 86             | 8                | 111           |
| RespWriter.cs               | 91.67% | 22     | 2        | 0       | 0           | 8       | 0              | 0              | 22             | 2                | 32            |
| Server\/Connection.cs       | 57.14% | 28     | 19       | 8       | 8           | 22      | 26             | 0              | 36             | 27               | 111           |
| Server\/RespServer.cs       | 21.43% | 2      | 11       | 1       | 0           | 5       | 10             | 0              | 3              | 11               | 29            |
| Storage\/ByteKeyComparer.cs | 16.67% | 2      | 2        | 0       | 8           | 3       | 0              | 0              | 2              | 10               | 15            |
| Storage\/Collections.cs     | 0.00%  | 0      | 0        | 0       | 6           | 4       | 0              | 0              | 0              | 6                | 10            |
| Storage\/Database.cs        | 56.77% | 70     | 44       | 18      | 23          | 41      | 25             | 0              | 88             | 67               | 221           |
| Storage\/GlobMatcher.cs     | 77.11% | 62     | 11       | 2       | 8           | 6       | 0              | 1              | 64             | 19               | 90            |
| Storage\/Keyspace.cs        | 44.44% | 4      | 2        | 0       | 3           | 7       | 0              | 0              | 4              | 5                | 16            |
| Storage\/RString.cs         | 80.90% | 72     | 14       | 0       | 3           | 23      | 5              | 0              | 72             | 17               | 117           |

## The final mutation score is 51.37%

### *Coverage Thresholds: high:90 low:75 break:0*
