// Os testes de persistência não rodam em paralelo entre si.
//
// Cada classe cria o próprio banco SQLCipher em pasta temporária, o que parece isolado — e
// não é. `SqliteConnection.ClearAllPools()`, que todas elas chamam ao terminar para soltar o
// arquivo e poder apagar a pasta, é **global ao processo**: limpa o pool de todas as strings
// de conexão, não só o da classe que chamou.
//
// O xUnit roda classes de teste diferentes em paralelo por padrão. O resultado é uma corrida:
// a classe A termina e limpa o pool no exato momento em que a classe B abre uma conexão
// reaproveitada dele. O handle nativo já foi descartado, e o `Open()` estoura com
//
//     System.ObjectDisposedException: Cannot access a disposed object.
//     Object name: 'SQLitePCL.sqlite3'.
//        at SQLitePCL.SQLite3Provider_e_sqlcipher...sqlite3_create_function
//        at Microsoft.Data.Sqlite.SqliteConnection.Open()
//
// Sempre numa classe diferente, sempre sem relação com o que o commit mudou — foi assim que
// ele apareceu, num commit que só alterava uma linha de JSON.
//
// A serialização é do assembly inteiro, e não por coleção, porque a alternativa depende de
// alguém lembrar de marcar cada classe nova. O custo é irrisório: a suíte já leva cerca de um
// minuto, dominada pelas migrações que cada teste executa contra um banco de verdade.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
