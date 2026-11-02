using FellowOakDicom;

namespace OIS.DicomScp.Scp;

/// <summary>
/// Storage SOP Class 白名单注册表：判断某抽象语法（Abstract Syntax UID）是否为可接收的 Storage SOP Class。
/// 配置 Scp:StorageServiceSopClasses 为 ["*"] 表示接受全部 Storage SOP Class，
/// 否则按显式列出的 UID 白名单过滤。
/// </summary>
public static class StorageSopClassRegistry
{
    /// <summary>
    /// 常见 Storage SOP Class UID 集合（DICOM PS3.4 / PS3.6）。
    /// 覆盖影像、放疗 RT 等主要存储类；配置 * 时使用本集合。
    /// </summary>
    private static readonly HashSet<string> KnownStorageSopClasses = new(StringComparer.Ordinal)
    {
        DicomUID.CTImageStorage.UID,
        DicomUID.EnhancedCTImageStorage.UID,
        DicomUID.MRImageStorage.UID,
        DicomUID.PositronEmissionTomographyImageStorage.UID,
        DicomUID.EnhancedPETImageStorage.UID,
        DicomUID.SecondaryCaptureImageStorage.UID,
        DicomUID.XRayAngiographicImageStorage.UID,
        DicomUID.DigitalXRayImageStorageForPresentation.UID,
        DicomUID.DigitalXRayImageStorageForProcessing.UID,
        DicomUID.ComputedRadiographyImageStorage.UID,
        DicomUID.VLPhotographicImageStorage.UID,
        DicomUID.UltrasoundImageStorage.UID,
        DicomUID.NuclearMedicineImageStorage.UID,
        DicomUID.RTImageStorage.UID,
        DicomUID.RTStructureSetStorage.UID,
        DicomUID.RTPlanStorage.UID,
        DicomUID.RTDoseStorage.UID,
        DicomUID.RTBeamsTreatmentRecordStorage.UID,
        DicomUID.RTTreatmentSummaryRecordStorage.UID,
        DicomUID.RTIonPlanStorage.UID,
        DicomUID.RTPhysicianIntentStorage.UID,
        DicomUID.RawDataStorage.UID,
        DicomUID.SpatialRegistrationStorage.UID,
        DicomUID.SegmentationStorage.UID,
        DicomUID.SurfaceSegmentationStorage.UID,
        DicomUID.OphthalmicPhotography8BitImageStorage.UID,
        DicomUID.OphthalmicTomographyImageStorage.UID,
    };

    /// <summary>
    /// 当前配置的 Storage SOP Class 白名单（来自 Scp:StorageServiceSopClasses）。
    /// 默认为 ["*"] 即全部 Storage SOP Class；程序启动时由配置覆盖。
    /// </summary>
    public static IReadOnlyCollection<string> Configured { get; set; } = new[] { "*" };

    /// <summary>判断某抽象语法是否为可接收的 Storage SOP Class。</summary>
    public static bool IsStorage(string? abstractSyntax)
    {
        if (string.IsNullOrWhiteSpace(abstractSyntax))
            return false;
        return KnownStorageSopClasses.Contains(abstractSyntax);
    }

    /// <summary>
    /// 判断某抽象语法是否在配置白名单内（* 表示全部 Storage SOP Class）。
    /// </summary>
    public static bool IsAllowed(string? abstractSyntax, IReadOnlyCollection<string> configured)
    {
        if (string.IsNullOrWhiteSpace(abstractSyntax))
            return false;

        if (configured.Any(x => x == "*"))
            return IsStorage(abstractSyntax);

        return configured.Contains(abstractSyntax, StringComparer.Ordinal);
    }
}