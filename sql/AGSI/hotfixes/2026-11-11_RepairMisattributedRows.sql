USE AGSI;
GO

-- =============================================================================
-- 2026-11-11  Repair arm.GasStorage rows corrupted by the pre-2026-09-28
--             entity-attribution bug.
--
-- WHAT HAPPENED
--   `?country=eu` and `?country=ne` both answer [eu, ne]; before the fix every
--   record in a body was stamped with the REQUESTING unit's EntityId, so the
--   aggregate values landed under the wrong entity. The days inside the 21-day hot
--   window were re-pulled and corrected after the fix went live; these were not,
--   because they had already aged out of it, so the loader will never touch them:
--     * eu  2026-08-30 .. 2026-09-14 (16 rows) hold the NON-EU (ne) values
--     * ES  2026-08-31               (1 row)   holds the aggregate value
--     * GB  1 row, RS 3 rows                   are aggregate copies. AGSI has no
--       data for either code: `gb` is UK Pre-Brexit (values end 2020-12; UK data
--       is entity GB*) and `rs` (Serbia) has only '-' placeholders.
--
-- HOW A ROW IS RECOGNISED AS CORRUPT
--   Its GasInStorage AND Full equal the `ne` row's for the same gas day. `ne` was
--   never corrupted (the eu/ne equality only ever went one way), and no real
--   country can equal the Non-EU total.
--
-- SAFETY
--   * Dry run by default: everything runs in one transaction and is ROLLED BACK
--     unless @Commit = 1. The before/after result sets show what would change.
--   * Re-runnable: rows already correct are skipped; a target row that is neither
--     corrupt nor already correct ABORTS the whole script without changing anything.
--   * Only the 17 listed rows are updated and only GB/RS rows that equal `ne` are
--     deleted. No other table is touched. FileLogId/DateCreated are left as they
--     are; ModifiedAtUtc is set to now.
--
-- CORRECT VALUES
--   Come from the AGSI API and are written into the GENERATED block below by
--   scripts\New-AgsiRepairValues.ps1 (needs the AGSI API key, so it is not
--   checked in pre-filled). Until that has been run the script refuses to proceed.
-- =============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;   -- 0 = dry run (rollback), 1 = apply

DECLARE @Fix TABLE
(
    Code               VARCHAR(16)   NOT NULL,
    GasDayStart        DATE          NOT NULL,
    Gas_Day            DATE          NOT NULL,
    UpdatedAt          DATETIME2(0)  NULL,
    GasDayEnd          DATE          NOT NULL,
    GasInStorage       DECIMAL(18,4) NULL,
    Consumption        DECIMAL(18,4) NULL,
    ConsumptionFull    DECIMAL(18,4) NULL,
    Injection          DECIMAL(18,4) NULL,
    Withdrawal         DECIMAL(18,4) NULL,
    NetWithdrawal      DECIMAL(18,4) NULL,
    WorkingGasVolume   DECIMAL(18,4) NULL,
    InjectionCapacity  DECIMAL(18,4) NULL,
    WithdrawalCapacity DECIMAL(18,4) NULL,
    ContractedCapacity DECIMAL(18,4) NULL,
    AvailableCapacity  DECIMAL(18,4) NULL,
    CoveredCapacity    DECIMAL(9,4)  NULL,
    [Status]           VARCHAR(4)    NOT NULL,
    Trend              DECIMAL(9,4)  NULL,
    [Full]             DECIMAL(9,4)  NULL,
    PRIMARY KEY (Code, GasDayStart)
);

-- BEGIN GENERATED VALUES (scripts\New-AgsiRepairValues.ps1 -- do not hand-edit)
-- generated 2026-11-11 18:54:27 from https://agsi.gie.eu by scripts\New-AgsiRepairValues.ps1
INSERT INTO @Fix (Code, GasDayStart, Gas_Day, UpdatedAt, GasDayEnd, GasInStorage, Consumption, ConsumptionFull,
                  Injection, Withdrawal, NetWithdrawal, WorkingGasVolume, InjectionCapacity, WithdrawalCapacity,
                  ContractedCapacity, AvailableCapacity, CoveredCapacity, [Status], Trend, [Full])
VALUES
    ('eu', '2026-08-30', '2026-11-10', '2026-10-27T18:31:54', '2026-08-31', 735.8081, 3519, 20.91, 4164.95, 121.8, -4043.2, 1130.2594, 12243.85, 20025.57, 1135.7843, 0.9562, 100, 'C', 0.36, 65.1),
    ('eu', '2026-08-31', '2026-11-10', '2026-10-30T06:00:07', '2026-09-01', 739.1553, 3519, 21, 3419.32, 99.4, -3319.9, 1130.2594, 12219.09, 20026.87, 1027.813, 108.9276, 100, 'C', 0.3, 65.4),
    ('eu', '2026-09-01', '2026-11-10', '2026-10-27T18:32:00', '2026-09-02', 741.9175, 3519, 21.08, 2702.23, 143.3, -2559, 1130.6108, 12218.54, 20027.89, 1034.6658, 101.5491, 100, 'C', 0.24, 65.62),
    ('eu', '2026-09-02', '2026-11-10', '2026-10-27T18:31:24', '2026-09-03', 744.3152, 3519, 21.15, 2636.78, 251.6, -2385.2, 1130.6108, 12218.15, 19942.85, 1034.6626, 101.5522, 100, 'C', 0.21, 65.83),
    ('eu', '2026-09-03', '2026-11-10', '2026-10-27T18:32:10', '2026-09-04', 746.9766, 3519, 21.23, 2800.47, 134.6, -2665.9, 1130.6108, 12242, 19942.99, 1034.6612, 101.5536, 100, 'C', 0.24, 66.07),
    ('eu', '2026-09-04', '2026-11-10', '2026-10-27T18:32:15', '2026-09-05', 750.1367, 3519, 21.32, 3269.59, 105.4, -3164.2, 1130.6108, 12241.93, 19943.04, 1034.7218, 101.493, 100, 'C', 0.28, 66.35),
    ('eu', '2026-09-05', '2026-11-10', '2026-10-27T18:32:19', '2026-09-06', 753.5132, 3519, 21.41, 3588.5, 207.2, -3381.3, 1131.5658, 12246.41, 20029.18, 1035.2431, 101.996, 100, 'C', 0.3, 66.59),
    ('eu', '2026-09-06', '2026-11-10', '2026-10-27T18:32:37', '2026-09-07', 757.1628, 3519, 21.52, 3779.22, 118.2, -3661, 1131.5658, 12242.82, 20027.01, 1035.2563, 101.9828, 100, 'C', 0.32, 66.91),
    ('eu', '2026-09-07', '2026-11-10', '2026-10-27T18:32:40', '2026-09-08', 759.7206, 3519, 21.59, 2729.8, 167.8, -2562, 1131.5658, 12243.29, 20026.12, 1035.2585, 101.9806, 100, 'E', 0.23, 67.14),
    ('eu', '2026-09-08', '2026-11-10', '2026-10-27T18:32:58', '2026-09-09', 762.0751, 3519, 21.66, 2595.08, 225.9, -2369.2, 1131.5658, 12243.95, 20025.16, 1035.258, 101.9811, 100, 'E', 0.21, 67.35),
    ('eu', '2026-09-09', '2026-11-10', '2026-10-27T18:33:01', '2026-09-10', 764.0774, 3519, 21.71, 2466.79, 343.1, -2123.7, 1131.5658, 12235.06, 19987.5, 1035.3396, 101.8995, 100, 'E', 0.18, 67.52),
    ('eu', '2026-09-10', '2026-11-10', '2026-10-27T18:33:04', '2026-09-11', 765.5638, 3519, 21.76, 2014.84, 522.5, -1492.4, 1131.5658, 12245.51, 20022.27, 1035.3379, 101.9012, 100, 'C', 0.13, 67.66),
    ('eu', '2026-09-11', '2026-11-10', '2026-10-27T18:33:07', '2026-09-12', 766.9648, 3519, 21.79, 2027.71, 630.7, -1397, 1131.5658, 12247.02, 20019.67, 1035.4524, 101.902, 100, 'E', 0.12, 67.78),
    ('eu', '2026-09-12', '2026-11-10', '2026-10-27T18:33:11', '2026-09-13', 769.9403, 3519, 21.88, 3150.66, 176.2, -2974.5, 1131.5658, 12248.72, 20016.74, 1035.6164, 101.738, 100, 'C', 0.26, 68.04),
    ('eu', '2026-09-13', '2026-11-10', '2026-10-27T18:33:18', '2026-09-14', 773.0159, 3519, 21.97, 3263.19, 190.7, -3072.5, 1131.5658, 12249.46, 20015.5, 1035.6206, 101.7338, 100, 'C', 0.27, 68.31),
    ('eu', '2026-09-14', '2026-11-10', '2026-10-16T13:38:28', '2026-09-15', 775.1252, 3519, 22.03, 2366.54, 246.4, -2120.2, 1131.5658, 12250.03, 20014.56, 1038.4263, 98.9282, 100, 'C', 0.19, 68.5),
    ('ES', '2026-08-31', '2026-11-10', '2026-09-01T10:46:43', '2026-09-01', 26.3177, 326.6000, 8.06, 0, 0, 0, 35.8318, 143.34, 208.96, 31.6443, 4.1876, 100, 'C', 0, 73.45)
;
-- END GENERATED VALUES

-- ---------------------------------------------------------------------------
-- 1. The fix set must be exactly the 17 expected rows.
-- ---------------------------------------------------------------------------
DECLARE @Expected TABLE (Code VARCHAR(16) NOT NULL, GasDayStart DATE NOT NULL, PRIMARY KEY (Code, GasDayStart));
INSERT INTO @Expected (Code, GasDayStart)
SELECT 'eu', DATEADD(DAY, n.n, CAST('2026-08-30' AS DATE))
  FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15)) AS n(n);
INSERT INTO @Expected (Code, GasDayStart) VALUES ('ES', '2026-08-31');

IF NOT EXISTS (SELECT 1 FROM @Fix)
    THROW 50001, 'No corrected values: run scripts\New-AgsiRepairValues.ps1 to fill the GENERATED block first.', 1;

IF EXISTS (SELECT Code, GasDayStart FROM @Expected EXCEPT SELECT Code, GasDayStart FROM @Fix)
   OR EXISTS (SELECT Code, GasDayStart FROM @Fix EXCEPT SELECT Code, GasDayStart FROM @Expected)
    THROW 50002, 'The GENERATED block does not hold exactly the 17 expected (Code, GasDayStart) rows.', 1;

-- ---------------------------------------------------------------------------
-- 2. Classify every target row against the same-day `ne` row.
-- ---------------------------------------------------------------------------
DECLARE @Plan TABLE
(
    EntityId     INT          NULL,
    Code         VARCHAR(16)  NOT NULL,
    GasDayStart  DATE         NOT NULL,
    Action       VARCHAR(16)  NOT NULL,   -- UPDATE / ALREADY_OK / ABORT
    StoredGis    DECIMAL(18,4) NULL,
    NeGis        DECIMAL(18,4) NULL,
    CorrectGis   DECIMAL(18,4) NULL
);

INSERT INTO @Plan (EntityId, Code, GasDayStart, Action, StoredGis, NeGis, CorrectGis)
SELECT e.Id, f.Code, f.GasDayStart,
       CASE
           WHEN g.EntityId IS NULL OR n.EntityId IS NULL                       THEN 'ABORT'
           WHEN f.GasInStorage = n.GasInStorage                                THEN 'ABORT'  -- fetched value IS the aggregate
           WHEN g.GasInStorage = n.GasInStorage AND g.[Full] = n.[Full]        THEN 'UPDATE'
           WHEN g.GasInStorage = f.GasInStorage AND ISNULL(g.[Full], -1) = ISNULL(f.[Full], -1) THEN 'ALREADY_OK'
           ELSE 'ABORT'
       END,
       g.GasInStorage, n.GasInStorage, f.GasInStorage
  FROM @Fix AS f
  LEFT JOIN arm.GasStorageEntity AS e  ON e.Code = f.Code
  LEFT JOIN arm.GasStorage       AS g  ON g.EntityId = e.Id AND g.GasDayStart = f.GasDayStart
  LEFT JOIN arm.GasStorageEntity AS ne ON ne.Code = 'ne'
  LEFT JOIN arm.GasStorage       AS n  ON n.EntityId = ne.Id AND n.GasDayStart = f.GasDayStart;

DECLARE @Delete TABLE (EntityId INT NOT NULL, Code VARCHAR(16) NOT NULL, GasDayStart DATE NOT NULL, StoredGis DECIMAL(18,4) NULL, PRIMARY KEY (EntityId, GasDayStart));

INSERT INTO @Delete (EntityId, Code, GasDayStart, StoredGis)
SELECT g.EntityId, e.Code, g.GasDayStart, g.GasInStorage
  FROM arm.GasStorage       AS g
  JOIN arm.GasStorageEntity AS e  ON e.Id = g.EntityId AND e.Code IN ('GB', 'RS')
  JOIN arm.GasStorageEntity AS ne ON ne.Code = 'ne'
  JOIN arm.GasStorage       AS n  ON n.EntityId = ne.Id AND n.GasDayStart = g.GasDayStart
 WHERE g.GasInStorage = n.GasInStorage
   AND g.[Full]       = n.[Full];

SELECT 'PLAN' AS Step, Code, GasDayStart, Action, StoredGis, NeGis, CorrectGis FROM @Plan
UNION ALL
SELECT 'PLAN', Code, GasDayStart, 'DELETE', StoredGis, StoredGis, NULL FROM @Delete
ORDER BY Code, GasDayStart;

-- GB/RS rows that do NOT look like aggregate copies are left alone; show them.
SELECT 'KEPT (not an aggregate copy)' AS Note, e.Code, g.GasDayStart, g.GasInStorage
  FROM arm.GasStorage AS g
  JOIN arm.GasStorageEntity AS e ON e.Id = g.EntityId AND e.Code IN ('GB', 'RS')
 WHERE NOT EXISTS (SELECT 1 FROM @Delete AS d WHERE d.EntityId = g.EntityId AND d.GasDayStart = g.GasDayStart);

IF EXISTS (SELECT 1 FROM @Plan WHERE Action = 'ABORT')
    THROW 50003, 'At least one target row is neither the corrupt aggregate copy nor already correct (see PLAN rows marked ABORT). Nothing was changed.', 1;

-- ---------------------------------------------------------------------------
-- 3. Apply (rolled back unless @Commit = 1).
-- ---------------------------------------------------------------------------
BEGIN TRANSACTION;

DELETE g
  FROM arm.GasStorage AS g
  JOIN @Delete AS d ON d.EntityId = g.EntityId AND d.GasDayStart = g.GasDayStart;
DECLARE @Deleted INT = @@ROWCOUNT;

UPDATE g
   SET g.Gas_Day            = f.Gas_Day,
       g.UpdatedAt          = f.UpdatedAt,
       g.GasDayEnd          = f.GasDayEnd,
       g.GasInStorage       = f.GasInStorage,
       g.Consumption        = f.Consumption,
       g.ConsumptionFull    = f.ConsumptionFull,
       g.Injection          = f.Injection,
       g.Withdrawal         = f.Withdrawal,
       g.NetWithdrawal      = f.NetWithdrawal,
       g.WorkingGasVolume   = f.WorkingGasVolume,
       g.InjectionCapacity  = f.InjectionCapacity,
       g.WithdrawalCapacity = f.WithdrawalCapacity,
       g.ContractedCapacity = f.ContractedCapacity,
       g.AvailableCapacity  = f.AvailableCapacity,
       g.CoveredCapacity    = f.CoveredCapacity,
       g.[Status]           = f.[Status],
       g.Trend              = f.Trend,
       g.[Full]             = f.[Full],
       g.ModifiedAtUtc      = SYSUTCDATETIME()
  FROM arm.GasStorage AS g
  JOIN @Plan AS p ON p.EntityId = g.EntityId AND p.GasDayStart = g.GasDayStart AND p.Action = 'UPDATE'
  JOIN @Fix  AS f ON f.Code = p.Code AND f.GasDayStart = p.GasDayStart;
DECLARE @Updated INT = @@ROWCOUNT;

IF @Updated <> (SELECT COUNT(*) FROM @Plan WHERE Action = 'UPDATE')
    THROW 50004, 'Updated row count does not match the plan; rolled back.', 1;

SELECT 'AFTER' AS Step, e.Code, g.GasDayStart, g.GasInStorage, g.[Full], g.[Status], g.ModifiedAtUtc
  FROM arm.GasStorage AS g
  JOIN arm.GasStorageEntity AS e ON e.Id = g.EntityId
  JOIN @Fix AS f ON f.Code = e.Code AND f.GasDayStart = g.GasDayStart
 ORDER BY e.Code, g.GasDayStart;

IF @Commit = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT CONCAT('COMMITTED: ', @Updated, ' row(s) updated, ', @Deleted, ' row(s) deleted.');
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT CONCAT('DRY RUN (rolled back): would update ', @Updated, ' row(s) and delete ', @Deleted, ' row(s). Set @Commit = 1 to apply.');
END
GO
