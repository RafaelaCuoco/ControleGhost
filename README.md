# Controle Ghost - ETL em Modo Fantasma 👻

Uma aplicação Windows Forms desenvolvida em .NET 10 para migração e transformação de dados invisível à infraestrutura.

O principal objetivo desta aplicação é atuar em "Modo Fantasma", garantindo o processamento constante (leitura de JSON -> conversão XML -> gravação no SQL Server) de grandes volumes de dados sem gerar **nenhum** tipo de pico ou alerta de CPU, Memória, Disco ou Rede no sistema operacional.

## 🛠️ Arquitetura e Técnicas Utilizadas

Esta aplicação atinge o nível zero de percepção de carga através da combinação rigorosa de 3 controles arquiteturais:

### 1. Sistema Operacional: Thread de Baixa Prioridade (CPU)
Utilizamos `TaskCreationOptions.LongRunning` em conjunto com a configuração `Thread.CurrentThread.Priority = ThreadPriority.Lowest`.
- **Por que isso é feito?** Para forçar a thread do processo de ETL a se descolar do ThreadPool normal da aplicação e rodar exclusivamente no tempo de "sobra" (idle) da CPU. Qualquer outra tarefa do servidor sempre terá prioridade sobre a migração.
- **Impacto:** O consumo da CPU é virtualmente zero se o servidor estiver sob stress ou atendendo usuários.

### 2. Controle de Fluxo: Bandwidth Throttling (Rede e Disco I/O)
Implementamos uma classe própria de monitoramento (`BandwidthThrottler`).
- **Por que isso é feito?** A prioridade da thread limita a CPU, mas não evita que puxemos ou joguemos volumes massivos de gigabytes pela placa de rede, derrubando a latência do banco de dados para outras aplicações. O _Throttler_ soma os bytes processados no segundo atual. Se ultrapassar o limite (ex: 2MB/s), ele força a Task a "dormir" até o fim do segundo.
- **Impacto:** Protege a placa de rede do servidor hospedeiro e do servidor SQL. As requisições de IOPS de leitura e escrita ficam niveladas (flat-line).

### 3. Backpressure de Concorrência: SemaphoreSlim (Memória)
Criamos um funil com limite estrito de gravações.
- **Por que isso é feito?** Mesmo puxando os dados do SQL em Stream (`CommandBehavior.SequentialAccess`), a etapa de gravação por lote (`SqlBulkCopy`) precisa despejar os dados no disco. Se a leitura e transformação forem mais rápidas que a escrita do SQL de destino, os objetos em memória começariam a se empilhar (estourando a RAM).
- **Impacto:** O Semáforo (configurado para 1 gravação simultânea) segura o loop de extração, impedindo o acúmulo infinito de Lotes em memória.

---

## 💻 Como Rodar e Testar a Aplicação

1. Certifique-se de ter o SDK do **.NET 10** instalado em sua máquina.
2. Clone o repositório ou navegue até a pasta `ControleGhost`.
3. Abra o arquivo `Form1.cs` e localize o evento `BtnStart_Click`.
4. Altere a variável `connectionString` com os dados do seu SQL Server e as tabelas reais.
5. Inicie o projeto pelo Visual Studio ou via CLI com:
   ```bash
   dotnet run
   ```
6. O formulário será aberto com uma janela de logs que exibirá os dados estruturais em tempo real.

### Visualizando no Painel (Log UI)
Ao clicar em **Iniciar Migração Invisível**, a janela começará a despejar métricas linha a linha. Exemplo:

```
[10:30:15] Registro 1205 processado | Rede trafegada (In/Out): 4.15 KB | CPU (conversão): 2ms | Banco: Adicionado ao lote
[10:30:15] Registro 1206 processado | Rede trafegada (In/Out): 6.02 KB | CPU (conversão): 5ms | Banco: Adicionado ao lote
```

Se a rede travar ou ocorrerem atrasos, você perceberá que a aplicação "descansa" para não inflar as métricas, permitindo o cancelamento a qualquer momento com o botão "Parar Migração".

---

## 🧩 Modularidade

A aplicação usa Interfaces limpas no diretório `/Core`:
- **`IJsonToXmlConverter`:** Permite que você implemente a melhor forma de gerar o XML (ex: usar serialização fortemente tipada, Xslt, Json.NET, etc.) e injete sem quebrar a lógica de throttling.
- **`IBandwidthThrottler`:** Pode ser customizado ou desligado dependendo do contexto.

---
_Desenvolvido para cenários de migração massiva sem janelas de parada (Zero Downtime / Ghost Migration)._
