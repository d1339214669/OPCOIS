namespace OIS.DicomScp.Scu;

/// <summary>C-ECHO 结果状态（AC-02/AC-03）。</summary>
public enum EchoStatus
{
    /// <summary>连通性验证成功（响应状态为 Success 0x0000）。</summary>
    Success,

    /// <summary>连通性验证失败（连接被拒/超时/AET 不匹配等，Reason 给可读原因）。</summary>
    Failed,
}

/// <summary>C-ECHO 结果。Reason 在失败时给可读原因（AC-03「失败必须可见」）。</summary>
public sealed record EchoResult(EchoStatus Status, string? Reason);

/// <summary>C-STORE 转发结果状态（AC-04/AC-06/AC-07）。</summary>
public enum StoreStatus
{
    /// <summary>转发成功（远端 SCP 实际收到该实例）。</summary>
    Success,

    /// <summary>转发失败（文件缺失/连接失败/AET 拒绝/响应非成功等），Reason 给可读原因。</summary>
    Failed,
}

/// <summary>C-STORE 转发结果。Reason 失败时给可读原因/状态码（AC-04/AC-07）。</summary>
public sealed record ForwardResult(StoreStatus Status, string? Reason);
