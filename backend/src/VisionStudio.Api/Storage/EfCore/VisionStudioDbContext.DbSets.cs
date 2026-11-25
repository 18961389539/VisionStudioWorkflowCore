using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore;

public partial class VisionStudioDbContext
{
    public DbSet<AlarmEntity> Alarms => Set<AlarmEntity>();
    public DbSet<CameraFeatureProfileEntity> CameraFeatureProfiles => Set<CameraFeatureProfileEntity>();
    public DbSet<CameraSyncGroupEntity> CameraSyncGroups => Set<CameraSyncGroupEntity>();
    public DbSet<CameraSyncRunEntity> CameraSyncRuns => Set<CameraSyncRunEntity>();
    public DbSet<CameraSyncCommissioningTestEntity> CameraSyncCommissioningTests => Set<CameraSyncCommissioningTestEntity>();
    public DbSet<MigrationStateEntity> MigrationStates => Set<MigrationStateEntity>();

    public DbSet<SecurityUserEntity> SecurityUsers => Set<SecurityUserEntity>();
    public DbSet<SecuritySessionEntity> SecuritySessions => Set<SecuritySessionEntity>();
    public DbSet<SecurityServiceTokenEntity> SecurityServiceTokens => Set<SecurityServiceTokenEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<JobVersionEntity> JobVersions => Set<JobVersionEntity>();
    public DbSet<JobPublicationHistoryEntity> JobPublicationHistory => Set<JobPublicationHistoryEntity>();
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<JobVersionValidationEntity> JobVersionValidations => Set<JobVersionValidationEntity>();
    public DbSet<ProductParameterValueEntity> ProductParameterValues => Set<ProductParameterValueEntity>();
    public DbSet<RecipeParameterValueEntity> RecipeParameterValues => Set<RecipeParameterValueEntity>();
    public DbSet<WorkflowModuleEntity> WorkflowModules => Set<WorkflowModuleEntity>();
    public DbSet<WorkflowModuleVersionEntity> WorkflowModuleVersions => Set<WorkflowModuleVersionEntity>();
    public DbSet<RuntimeDependencyManifestEntity> RuntimeDependencyManifests => Set<RuntimeDependencyManifestEntity>();
    public DbSet<RecipeDeliveryReceiptEntity> RecipeDeliveryReceipts => Set<RecipeDeliveryReceiptEntity>();
    public DbSet<RecipeRolloutBatchEntity> RecipeRolloutBatches => Set<RecipeRolloutBatchEntity>();
    public DbSet<RecipeRolloutStationEntity> RecipeRolloutStations => Set<RecipeRolloutStationEntity>();
    public DbSet<RecipeRolloutLatestStationEntity> RecipeRolloutLatestStations => Set<RecipeRolloutLatestStationEntity>();

    public DbSet<CalibrationAssetEntity> CalibrationAssets => Set<CalibrationAssetEntity>();
    public DbSet<CalibrationVersionEntity> CalibrationVersions => Set<CalibrationVersionEntity>();
    public DbSet<CalibrationPublicationHistoryEntity> CalibrationPublicationHistory => Set<CalibrationPublicationHistoryEntity>();
    public DbSet<CameraIntrinsicAssetEntity> CameraIntrinsicAssets => Set<CameraIntrinsicAssetEntity>();
    public DbSet<CameraIntrinsicVersionEntity> CameraIntrinsicVersions => Set<CameraIntrinsicVersionEntity>();
    public DbSet<CameraIntrinsicPublicationHistoryEntity> CameraIntrinsicPublicationHistory => Set<CameraIntrinsicPublicationHistoryEntity>();
    public DbSet<HandEyeCalibrationAssetEntity> HandEyeCalibrationAssets => Set<HandEyeCalibrationAssetEntity>();
    public DbSet<HandEyeCalibrationVersionEntity> HandEyeCalibrationVersions => Set<HandEyeCalibrationVersionEntity>();
    public DbSet<HandEyeCalibrationPublicationHistoryEntity> HandEyeCalibrationPublicationHistory => Set<HandEyeCalibrationPublicationHistoryEntity>();
    public DbSet<HandEye3DCalibrationAssetEntity> HandEye3DCalibrationAssets => Set<HandEye3DCalibrationAssetEntity>();
    public DbSet<HandEye3DCalibrationVersionEntity> HandEye3DCalibrationVersions => Set<HandEye3DCalibrationVersionEntity>();
    public DbSet<HandEye3DCalibrationPublicationHistoryEntity> HandEye3DCalibrationPublicationHistory => Set<HandEye3DCalibrationPublicationHistoryEntity>();
    public DbSet<AssetEntity> Assets => Set<AssetEntity>();
    public DbSet<AssetVersionEntity> AssetVersions => Set<AssetVersionEntity>();
    public DbSet<AssetBlobEntity> AssetBlobs => Set<AssetBlobEntity>();
    public DbSet<ShapeModelAssetEntity> ShapeModelAssets => Set<ShapeModelAssetEntity>();
    public DbSet<ShapeModelVersionEntity> ShapeModelVersions => Set<ShapeModelVersionEntity>();
    public DbSet<ShapeModelBlobEntity> ShapeModelBlobs => Set<ShapeModelBlobEntity>();

    public DbSet<RunTraceEntity> RunTraces => Set<RunTraceEntity>();
    public DbSet<RunNodeObservationEntity> RunNodeObservations => Set<RunNodeObservationEntity>();
    public DbSet<InvestigationCaseEntity> InvestigationCases => Set<InvestigationCaseEntity>();
    public DbSet<InvestigationCaseRunEntity> InvestigationCaseRuns => Set<InvestigationCaseRunEntity>();
    public DbSet<InvestigationCaseVerificationEntity> InvestigationCaseVerifications => Set<InvestigationCaseVerificationEntity>();
    public DbSet<ResultRecordEntity> ResultRecords => Set<ResultRecordEntity>();
    public DbSet<DurableCounterEventEntity> DurableCounterEvents => Set<DurableCounterEventEntity>();

    public DbSet<ValidationDatasetEntity> ValidationDatasets => Set<ValidationDatasetEntity>();
    public DbSet<ValidationDatasetItemEntity> ValidationDatasetItems => Set<ValidationDatasetItemEntity>();
    public DbSet<ValidationRunEntity> ValidationRuns => Set<ValidationRunEntity>();
    public DbSet<ValidationResultEntity> ValidationResults => Set<ValidationResultEntity>();
    public DbSet<PluginBenchmarkRunEntity> PluginBenchmarkRuns => Set<PluginBenchmarkRunEntity>();
    public DbSet<PluginBenchmarkPoolResultEntity> PluginBenchmarkPoolResults => Set<PluginBenchmarkPoolResultEntity>();
}
