using System.IO;
using Salvini.IoTDB;
using Salvini.IoTDB.Data;

namespace Salvini.IoTDB.Templates;

public class MeasurementNode : TemplateNode
{
    private DataType dataType;
    private Encoding encoding;
    private Compressor compressor;
    public MeasurementNode(string name, DataType dataType, Encoding encoding, Compressor compressor) : base(name)
    {
        this.dataType = dataType;
        this.encoding = encoding;
        this.compressor = compressor;
    }
    public override bool isMeasurement()
    {
        return true;
    }
    public DataType DataType
    {
        get
        {
            return dataType;
        }
    }
    public Encoding Encoding
    {
        get
        {
            return encoding;
        }
    }
    public Compressor Compressor
    {
        get
        {
            return compressor;
        }
    }

    public override byte[] ToBytes()
    {
        var buffer = new ByteBuffer();
        buffer.AddStr(this.Name);
        buffer.AddByte((byte)this.DataType);
        buffer.AddByte((byte)this.Encoding);
        buffer.AddByte((byte)this.Compressor);
        return buffer.GetBuffer();
    }
}
