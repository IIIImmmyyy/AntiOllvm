// See https://aka.ms/new-console-template for more information


// Console.WriteLine("Hello, World!");


using AntiOllvm.Helper;

namespace AntiOllvm
{
    internal static class Program
    {
        
        public static Config ParseArguments(string[] args)
        {
            Config config = new Config();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-s":
                        if (i + 1 < args.Length)
                        {
                            config.ida_cfg_path = args[i + 1];
                            i++; // 跳过下一个参数，因为它是值
                        }
                        else
                        {
                            throw new ArgumentException("错误: -s 参数缺少值。");
                        }
                        break;
                   
                    case "-force_no_child_main":
                        config.force_no_child_main = true;
                        break;
                    // 根据需要处理更多参数
                    default:
                        Console.WriteLine($"warning unknow arg {args[i]}");
                        break;
                }
            }

            // 设置默认值（如果需要）
            if (string.IsNullOrEmpty(config.fix_outpath))
            {
                config.fix_outpath = Path.Combine(Directory.GetCurrentDirectory(), "fix.json");
            }

            return config;
        }
        public static void Test()
        {
            Config config = new Config();
            config.ida_cfg_path = @"E:\RiderDemo\AntiOllvm\AntiOllvm\cfg_output_0x17f46c.json";
            config.fix_outpath = @"E:\RiderDemo\AntiOllvm\AntiOllvm\fix.json";
            App.Init(config);
        }

        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                try
                {
                    Config config = ParseArguments(args);
                    App.Init(config);
                }
                catch (ArgumentException ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
            else
            {
                Console.WriteLine("Usage: AntiOllvm -s <path> [-force_no_child_main]");
            }
        }
    }
}