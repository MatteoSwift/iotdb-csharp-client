using System.Collections;
using System.Collections.Concurrent;

namespace Salvini.IoTDB;

public class ClientPool:IEnumerable<Client>
{
    private ConcurrentQueue<Client> _clients { get; } = new ConcurrentQueue<Client>();
 
    public void Add(Client client)
    {
        Monitor.Enter(_clients);
        _clients.Enqueue(client);
        Monitor.Pulse(_clients);
        Monitor.Exit(_clients);
    } 

    public Client Take()
    {
        Monitor.Enter(_clients);
        if (_clients.IsEmpty)
        {
            Monitor.Wait(_clients);
        }
        _clients.TryDequeue(out var client);
        Monitor.Exit(_clients);
        return client;
    }

    public void Clear()
    {
        foreach (var client in _clients)
        {
            try
            {
                client?.Transport?.Close();
            }
            catch (System.Exception e)
            {

            }
        }
        _clients.Clear();
    }

    public IEnumerator<Client> GetEnumerator()
    {
        return _clients.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _clients.GetEnumerator();
    }
}
