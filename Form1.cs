using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ControleGhost.Core;

namespace ControleGhost
{
    public partial class Form1 : Form
    {
        private Button btnStart;
        private ListBox lstLog;
        private CancellationTokenSource _cts;

        public Form1()
        {
            InitializeComponent();
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Controle Ghost - Painel de Monitoramento (Modo Fantasma)";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;

            btnStart = new Button
            {
                Text = "Iniciar Migração Invisível",
                Location = new Point(20, 20),
                Size = new Size(200, 40),
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            btnStart.Click += BtnStart_Click;
            
            Label lblLog = new Label
            {
                Text = "Logs de Processamento (Rede, CPU e Banco):",
                Location = new Point(20, 70),
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Regular)
            };

            lstLog = new ListBox
            {
                Location = new Point(20, 95),
                Size = new Size(740, 440),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 9, FontStyle.Regular),
                HorizontalScrollbar = true
            };

            this.Controls.Add(btnStart);
            this.Controls.Add(lblLog);
            this.Controls.Add(lstLog);
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (btnStart.Text == "Parar Migração")
            {
                _cts?.Cancel();
                Log("Solicitando parada...");
                return;
            }

            btnStart.Text = "Parar Migração";
            lstLog.Items.Clear();
            Log("Iniciando Modo Fantasma (Baixa prioridade, restrição de CPU/Rede/Memória)...");

            _cts = new CancellationTokenSource();

            // Configuração das Injeções de Dependência
            var converter = new MockJsonToXmlConverter();
            var throttler = new BandwidthThrottler(maxMegabytesPerSecond: 2); // 2 MB/s max
            
            // ATENÇÃO: Coloque sua connection string real aqui
            string connectionString = "Server=MEU_SERVIDOR;Database=MEU_BANCO;Integrated Security=True;TrustServerCertificate=True;";
            var orchestrator = new GhostModeEtlOrchestrator(connectionString, converter, throttler);

            // Interface IProgress para capturar atualizações sem travar a UI Thread
            var progress = new Progress<string>(message => 
            {
                Log(message);
            });

            try
            {
                // Dispara o processo pesado
                await orchestrator.ExecuteGhostModeAsync(progress, _cts.Token);
                Log("Processamento finalizado com sucesso!");
            }
            catch (OperationCanceledException)
            {
                Log("Processamento abortado pelo usuário.");
            }
            catch (Exception ex)
            {
                Log($"ERRO (Verifique o Banco de Dados): {ex.Message}");
            }
            finally
            {
                btnStart.Text = "Iniciar Migração Invisível";
            }
        }

        private void Log(string message)
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            
            // Adiciona no topo da UI para melhor visualização
            lstLog.Items.Insert(0, logLine);

            // Salva em arquivo físico para histórico (fire and forget para não travar a UI)
            _ = Task.Run(() =>
            {
                try
                {
                    File.AppendAllText("ControleGhost_Log.txt", logLine + Environment.NewLine);
                }
                catch
                {
                    // Ignora falhas de escrita (ex: arquivo em uso) para não derrubar o Ghost Mode
                }
            });
        }
    }

    /// <summary>
    /// Implementação Mock do conversor apenas para injeção de dependência funcionar na UI.
    /// </summary>
    public class MockJsonToXmlConverter : IJsonToXmlConverter
    {
        public string Convert(string json)
        {
            // Simula um delay de CPU (conversão complexa)
            Thread.SpinWait(50000); 
            return "<xml>Mock Result</xml>";
        }
    }
}
