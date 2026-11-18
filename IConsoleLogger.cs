namespace ConsoleIntegration
{
    public interface IConsoleLogger
    {
        void Msg(string msg);
        void Warning(string msg);
        void Error(string msg);
    }
}