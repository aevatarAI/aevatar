namespace Aevatar.App.Application.Services;

public sealed class AppQuotaOptions
{
    public int MaxSavedEntities { get; set; } = 10;
    public int MaxEntitiesPerWeek { get; set; } = 3;
    public int MaxOperationsPerDay { get; set; } = 3;

    public void Normalize()
    {
        if (MaxSavedEntities <= 0)
            MaxSavedEntities = 10;
        if (MaxEntitiesPerWeek <= 0)
            MaxEntitiesPerWeek = 3;
        if (MaxOperationsPerDay <= 0)
            MaxOperationsPerDay = 3;
    }
}
