using System;
using Salvini.IoTDB;

namespace Salvini.IoTDB.Sample
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("test");
            var session = new Session(password: "admin#123", host: "192.168.0.11");
            await session.OpenAsync();
            using var ds = await session.ExecuteQueryStatementAsync("select 2MW from root.lz limit 10");
            var matrix = ds.ReadAsMatrix();

            for (var i = 0; i < matrix.Length / matrix.Rank; i++)
            {
                for (var j = 0; j < matrix.Rank; j++)
                {
                    Console.Write(matrix[i, j]);
                    Console.Write('\t');
                }
                Console.WriteLine();
            }
        }
    }
}

