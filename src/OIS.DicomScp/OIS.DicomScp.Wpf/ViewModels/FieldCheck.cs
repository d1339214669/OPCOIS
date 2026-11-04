namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 详情核对报告的一行：某关键标签在「DICOM 文件」与「库记录」中的取值及是否一致（AC-03）。
/// FileValue/DbValue 为原样字符串；缺失为 null（显示为空，不臆造，AC-09）。
/// </summary>
public sealed record FieldCheck(string Field, string? FileValue, string? DbValue, bool IsConsistent)
{
    /// <summary>显示用结果文本。</summary>
    public string ResultText => IsConsistent ? "一致" : "不一致";
}
