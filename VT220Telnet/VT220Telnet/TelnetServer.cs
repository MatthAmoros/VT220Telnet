using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VT220Telnet
{
    public partial class TelnetServer : ServiceBase
    {
        public TelnetServer()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            StartServer();
        }

        protected override void OnStop()
        {
            tokenSource.Cancel();
        }

        #region Server methods
        /// <summary>
        /// Telnet port, default should be 23 but ...
        /// </summary>
        const int PORT_NO = 8023;
        /// <summary>
        /// Token source to manage tasks abort queries
        /// </summary>
        static CancellationTokenSource tokenSource;
        /// <summary>
        /// Task list, to have a link of current ongoing tasks
        /// </summary>
        private static List<Task> _tasks;

        /// <summary>
        /// Commands enum
        /// </summary>
        private enum Commands
        {
            Process,
            Quality,
            Expedition,
            Exit
        }

        /// <summary>
        /// Start server
        /// Initialize ressources and tcp socket
        /// Accepts clients and create a new task for each
        /// </summary>
        private static void StartServer()
        {
            //Initialize ressources
            tokenSource = new CancellationTokenSource();
            _tasks = new List<Task>();

            //Task definition
            Action<object> action = (object clientTask) =>
            {
                BeginClientCommunication(clientTask as TcpClient);
            };

            //Initialize TCP Listener
            TcpListener listener = new TcpListener(IPAddress.Any, PORT_NO);
            Console.WriteLine("Listening...");

            listener.Start();

            do
            {
                //Blocking call
                TcpClient client = listener.AcceptTcpClient();

                //Avoid CPU high load
                Thread.Sleep(100);

                Console.WriteLine(">> " + client.Client.RemoteEndPoint.ToString() + " connected.");

                var clientWorker = new Task(action, client, tokenSource.Token);

                //Add to tasks list and start
                _tasks.Add(clientWorker);

                //Start client monitor
                clientWorker.Start();

                //If at least one task is running, setup a cleaning thread
                if (_tasks.Count == 1)
                {
                    //Cleaning task
                    var cleaningTask = new Task(() =>
                    {
                        Console.WriteLine("Starting cleaning tasks...");
                        do
                        {
                            Thread.Sleep(60000);
                            Console.WriteLine("Cleaning...");
                            _tasks.RemoveAll(x => x.IsCompleted);
                        } while (_tasks.Any());
                        Console.WriteLine("All tasks completed !");
                    });
                    cleaningTask.Start();
                }
            } while (_tasks.Any());

            listener.Stop();
        }

        /// <summary>
        /// For VT220 emulator
        /// </summary>
        private static string VT220_CLEAN_CONSOLE = (char)0x1B + "[2J";

        /// <summary>
        /// Handle communication with a TcpClient
        /// This function is executed on a sperated thread
        /// 
        /// Check cancellation, send command list, handle client inputs
        /// </summary>
        /// <param name="client"></param>
        static void BeginClientCommunication(TcpClient client)
        {
            //Save endpoint information
            string endPoint = client.Client.RemoteEndPoint.ToString();

            using (NetworkStream nwStream = client.GetStream())
            {
                do
                {
                    if (tokenSource.Token.IsCancellationRequested)
                    {
                        SendText(client, nwStream, "Server is shutting down...");
                        Console.WriteLine(">> " + endPoint + " disconnected by server.");
                        tokenSource.Token.ThrowIfCancellationRequested();
                    }

                    DisplayCommandList(client, nwStream);
                    HandleCommands(client, nwStream);
                } while (client.Connected);
                Console.WriteLine(">> " + endPoint + " disconnected.");
            }
        }

        /// <summary>
        /// Send command list
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        static void DisplayCommandList(TcpClient client, NetworkStream stream)
        {
            SendText(client, stream, "[1] FUNCTION 1" + Environment.NewLine + "[2] FUNCTION 2" + Environment.NewLine + "[3] FUNCTION 3" + Environment.NewLine + "[0] EXIT" + Environment.NewLine);
            Console.WriteLine(">> Sending command list to : " + client.Client.RemoteEndPoint.ToString());
        }

        #region I/O
        /// <summary>
        /// Basic output
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        /// <param name="text"></param>
        private static void SendText(TcpClient client, NetworkStream stream, string text)
        {
            byte[] buffer = new byte[text.Length];
            buffer = Encoding.ASCII.GetBytes(text);
            stream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Basic input
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        private static string GetInput(TcpClient client, NetworkStream stream)
        {
            byte[] buffer = new byte[client.ReceiveBufferSize];

            //Read incomming data
            int bytesRead = stream.Read(buffer, 0, client.ReceiveBufferSize);

            //Until line break
            string dataReceived = Encoding.ASCII.GetString(buffer, 0, bytesRead);
            while (!dataReceived.Contains('\r') || dataReceived.Length > 256)
            {
                bytesRead = stream.Read(buffer, 0, client.ReceiveBufferSize);
                dataReceived += Encoding.ASCII.GetString(buffer, 0, bytesRead);
            }

            //Trim /r char
            return dataReceived.Trim();
        }
        #endregion

        /// <summary>
        /// Re-root command to enum
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        static void HandleCommands(TcpClient client, NetworkStream stream)
        {
            string dataReceived = GetInput(client, stream);

            Console.WriteLine("Received : " + dataReceived);

            switch (dataReceived)
            {
                case "1":
                    HandleCommand(client, stream, Commands.Process);
                    break;
                case "2":
                    HandleCommand(client, stream, Commands.Quality);
                    break;
                case "3":
                    HandleCommand(client, stream, Commands.Expedition);
                    break;
                case "0":
                    HandleCommand(client, stream, Commands.Exit);
                    break;
            }
        }

        /// <summary>
        /// Re-root enum to behavior
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        /// <param name="command"></param>
        static void HandleCommand(TcpClient client, NetworkStream stream, Commands command)
        {
            switch (command)
            {
                case Commands.Process:
                    HandleFunction002(client, stream);
                    break;
                case Commands.Quality:
                    HandleFUnction001(client, stream);
                    break;
                case Commands.Expedition:
                    HandleFunction003(client, stream);
                    break;

                case Commands.Exit:
                    client.Close();
                    break;
            }

            if (command != Commands.Exit)
            {
                //Waiting for validation
                SendText(client, stream, ">> OK !" + Environment.NewLine);
                GetInput(client, stream);
                SendText(client, stream, VT220_CLEAN_CONSOLE);
            }
        }

        #region Commands
        //Business behavior

        /// <summary>
        /// SKU Quality check
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        private static string HandleFUnction001(TcpClient client, NetworkStream stream)
        {
            string sku;
            SendText(client, stream, ">> SKU ?" + Environment.NewLine);
            sku = GetInput(client, stream);
            Console.WriteLine(">> Quality Check OK : " + sku);
            return sku;
        }

        /// <summary>
        /// Example, bind SKU to production line and process
        /// </summary>
        /// <param name="client"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        private static string HandleFunction002(TcpClient client, NetworkStream stream)
        {
            SendText(client, stream, ">> Line ? (1,2,3)" + Environment.NewLine);
            string line = GetInput(client, stream);
            SendText(client, stream, ">> Process ?" + Environment.NewLine);
            string process = GetInput(client, stream);
            SendText(client, stream, ">> SKU ?" + Environment.NewLine);
            string sku = GetInput(client, stream);
            Console.WriteLine(">> Binding : " + sku + " | " + process + " | L" + line);
            SendText(client, stream, ">> Binding : " + sku + " | " + process + " | L" + line + Environment.NewLine);
            
            //TODO : Add some repository functions

            return sku;
        }

        private static string HandleFunction003(TcpClient client, NetworkStream stream)
        {
            string SKU;
            List<string> SKUs = new List<string>();
            SendText(client, stream, ">> Expedition order ?" + Environment.NewLine);
            string order = GetInput(client, stream);

            SendText(client, stream, ">> SKU ? (0 = End)" + Environment.NewLine);

            do
            {
                SKU = GetInput(client, stream);
                if (!string.IsNullOrEmpty(SKU))
                {
                    if (SKUs.Contains(SKU))
                        SendText(client, stream, ">> " + SKU + " already scanned." + Environment.NewLine);
                    SKUs.Add(SKU);
                }
                SendText(client, stream, Environment.NewLine);
            } while (!(string.IsNullOrEmpty(SKU) || SKU == "0"));

            Console.WriteLine(">> Expedition order " + order + " : ");
            SendText(client, stream, ">> Expedition order " + order + " : " + Environment.NewLine);

            foreach (var lt in SKUs)
            {
                Console.WriteLine("     >> SKU : " + lt);
                SendText(client, stream, "      >> SKU :" + lt + Environment.NewLine);
            }

            return SKU;
        }
        #endregion
        #endregion
    }
}
