using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ControleGhost.Core
{
    /// <summary>
    /// Orquestrador principal do ETL executando no "Modo Fantasma".
    /// </summary>
    public class GhostModeEtlOrchestrator
    {
        private readonly string _connectionString;
        private readonly IJsonToXmlConverter _converter;
        private readonly IBandwidthThrottler _throttler;
        
        // Configurações do Modo Fantasma
        private const int BATCH_SIZE = 100; // Lotes pequenos para baixo impacto de memória
        private const int MAX_CONCURRENT_WRITES = 1; // Máximo de escritas simultâneas no BD

        public GhostModeEtlOrchestrator(
            string connectionString, 
            IJsonToXmlConverter converter, 
            IBandwidthThrottler throttler)
        {
            _connectionString = connectionString;
            _converter = converter;
            _throttler = throttler;
        }

        /// <summary>
        /// Inicia o processo em background total.
        /// </summary>
        public async Task ExecuteGhostModeAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            // Criamos uma Task de longa duração (LongRunning). Isso avisa o runtime do .NET 
            // para criar uma Thread dedicada (fora do ThreadPool normal), evitando 
            // estrangular as threads usadas por requisições web ou outras partes do sistema.
            await Task.Factory.StartNew(async () =>
            {
                /* 
                 * [1] CONTROLE DO SISTEMA OPERACIONAL (Prioridade da Thread)
                 * Definir a prioridade como Lowest instrui o agendador de processos do Windows
                 * a só dar tempo de CPU para esta Thread se nenhuma outra Thread de prioridade
                 * maior estiver precisando. Isso garante que o ETL seja 100% invisível 
                 * para os usuários e outras aplicações rodando no servidor.
                 */
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                Thread.CurrentThread.IsBackground = true;
                
                await RunPipelineCoreAsync(progress, cancellationToken);
                
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        private async Task RunPipelineCoreAsync(IProgress<string> progress, CancellationToken ct)
        {
            /*
             * [2] CONTROLE DE CONCORRÊNCIA (SemaphoreSlim)
             * O SemaphoreSlim atua como uma barreira física de backpressure.
             * Ao configurá-lo para 1, nós garantimos que o banco de destino só receba 
             * um lote de INSERT (ou SqlBulkCopy) por vez. Se a leitura e transformação forem 
             * mais rápidas que a escrita no disco do banco de dados, a pipeline trava (espera) 
             * aqui de forma assíncrona, evitando esgotamento de memória RAM.
             */
            using var semaphore = new SemaphoreSlim(MAX_CONCURRENT_WRITES, MAX_CONCURRENT_WRITES);

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // READPAST previne lock de tabelas bloqueando a leitura de linhas travadas
            string query = "SELECT Id, JsonPayload FROM Origem.TabelaJson WITH (READPAST) ORDER BY Id";
            using var cmd = new SqlCommand(query, connection);
            
            // CommandBehavior.SequentialAccess impede que os dados NVARCHAR(MAX) subam
            // todos para a memória de uma vez. Os dados são lidos como stream.
            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

            var batchTable = CreateBatchTable();
            int rowCount = 0;

            while (await reader.ReadAsync(ct))
            {
                int id = reader.GetInt32(0);
                string json = reader.GetString(1); 
                
                int jsonBytes = Encoding.UTF8.GetByteCount(json);
                
                /*
                 * [3] CONTROLE DE FLUXO (Bandwidth Throttling) - LEITURA
                 * Ao calcular o peso (bytes) do payload e passar no throttler,
                 * limitamos a velocidade com que puxamos dados da rede do SQL Server.
                 */
                await _throttler.ThrottleAsync(jsonBytes, ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Transformação (dependência injetada)
                string xml = _converter.Convert(json);
                sw.Stop();
                
                int xmlBytes = Encoding.UTF8.GetByteCount(xml);

                batchTable.Rows.Add(id, xml);
                rowCount++;

                // Reporta na UI o custo de cada linha
                progress?.Report($"Registro {id} processado | Rede trafegada (In/Out): {(jsonBytes + xmlBytes) / 1024.0:F2} KB | CPU (conversão): {sw.ElapsedMilliseconds}ms | Banco: Adicionado ao lote");

                /*
                 * [4] CONTROLE DE FLUXO (Bandwidth Throttling) - ESCRITA
                 * Adicionamos o custo do XML à quota do throttler. A aplicação agora 
                 * respeita um teto máximo de transferência total (In + Out) por segundo.
                 */
                await _throttler.ThrottleAsync(xmlBytes, ct);

                if (rowCount >= BATCH_SIZE)
                {
                    // Espera liberação para não inundar o servidor destino
                    await semaphore.WaitAsync(ct); 
                    
                    var tableToWrite = batchTable;
                    batchTable = CreateBatchTable(); // Inicia novo lote vazio
                    rowCount = 0;

                    // Despacha a escrita e libera o semáforo ao finalizar
                    _ = Task.Run(async () =>
                    {
                        try 
                        { 
                            await WriteBatchToDestinationAsync(tableToWrite, ct); 
                        }
                        finally 
                        { 
                            semaphore.Release(); 
                        }
                    }, ct);
                }
            }

            // Processa o lote final residual
            if (rowCount > 0)
            {
                await semaphore.WaitAsync(ct);
                try 
                { 
                    await WriteBatchToDestinationAsync(batchTable, ct); 
                }
                finally 
                { 
                    semaphore.Release(); 
                }
            }
        }

        private async Task WriteBatchToDestinationAsync(DataTable table, CancellationToken ct)
        {
            using var bulkCopy = new SqlBulkCopy(_connectionString)
            {
                DestinationTableName = "Destino.TabelaXml",
                BatchSize = BATCH_SIZE,
                // Como forçamos o processo a ser intencionalmente lento, 
                // o timeout deve ser generoso para não abortar operações demoradas pelo Throttling
                BulkCopyTimeout = 600 
            };
            
            bulkCopy.ColumnMappings.Add("Id", "Id");
            bulkCopy.ColumnMappings.Add("XmlPayload", "XmlPayload");

            await bulkCopy.WriteToServerAsync(table, ct);
        }

        private DataTable CreateBatchTable()
        {
            var table = new DataTable();
            table.Columns.Add("Id", typeof(int));
            table.Columns.Add("XmlPayload", typeof(string)); // NVARCHAR(MAX) destino
            return table;
        }
    }
}
