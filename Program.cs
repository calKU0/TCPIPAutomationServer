using System.ServiceProcess;

namespace TCPIPAutomatykaServer
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main()
        {

            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new TCPServer()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
