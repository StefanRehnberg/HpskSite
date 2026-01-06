# Technical Specification

## Handicap System for Precision Pistol Shooting (SPSF)

### 1. Purpose

This document specifies a **handicap scoring system** for precision pistol shooting competitions organized under Svenska Pistolskytteförbundet (SPSF) rules.

The system is designed to:

* Allow fair competition across skill levels
* Support **live ranking updates after each series**
* Work with **electronic targets**
* Be deterministic, transparent, and auditable
* Be implemented in a **.NET-based application**

This system is an **overlay** and does not replace official SPSF scoring or class rules.

---

## 2. Scope & Assumptions

### In Scope

* Precision pistol competitions using 5-shot series
* Match lengths of 6, 7, or 10 series (300 / 350 / 500 max points)
* Regional competitions
* Individual scoring (no team logic)

### Out of Scope

* Field shooting (fält)
* Standard pistol time rules
* Official SPSF class advancement logic

---

## 3. Core Concepts

### Definitions

* **Series**: One 5-shot scoring unit
* **Raw Score**: Actual points shot (0–50 per series)
* **Handicap**: Bonus points added per series
* **Final Score**: Raw Score + Handicap
* **Reference Performance**: 48.0 points per series
* **Provisional Shooter**: Shooter with insufficient historical data

---

## 4. Handicap Calculation Logic

### 4.1 Reference Value

```
REFERENCE_SERIES_SCORE = 48.0
```

### 4.2 Shooter Average

* Calculated as **average points per series**
* Based on historical completed matches
* Discipline-specific (precision ≠ other disciplines)

---

### 4.3 Handicap per Series

```
HandicapPerSeries = REFERENCE_SERIES_SCORE − ShooterAveragePerSeries
```

### 4.3.1 Rounding

Handicap is rounded to the nearest **quarter-point (0.25 increments)**:

```
Valid values: x.0, x.25, x.5, x.75
Formula: Math.Round(value × 4) / 4
```

Examples:
| Raw Calculation | Rounded Handicap |
|-----------------|------------------|
| 5.12            | +5.0             |
| 5.13            | +5.25            |
| 5.37            | +5.25            |
| 5.38            | +5.5             |
| 0.43            | +0.5             |
| -2.37           | -2.25            |

### 4.4 Handicap Cap

```
MAX_HANDICAP_PER_SERIES = +5.0
```

If `HandicapPerSeries > MAX_HANDICAP_PER_SERIES`, clamp to `+5.0`.

Negative handicap values are allowed (no lower bound required).

---

### 4.5 Handicap Application

* Handicap is **fixed before match start**
* Same value applied to **every series**
* Handicap is added **after each completed series**

---

## 5. Provisional Handicap System

### 5.1 Provisional Criteria

A shooter is **provisional** if:

```
CompletedMatches < REQUIRED_MATCHES
```

Recommended:

```
REQUIRED_MATCHES = 8
```

---

### 5.2 Provisional Baselines (Per Series)

| Shooter Class | Provisional Average |
| ------------- | ------------------- |
| Class 1       | 40.0                |
| Class 2       | 44.0                |
| Class 3       | 47.0                |

---

### 5.3 Weighted Convergence Formula

Used to compute an **Effective Average** during provisional period:

```
EffectiveAverage =
( ProvisionalAverage × (REQUIRED_MATCHES − CompletedMatches)
  + ActualAverage × CompletedMatches
) / REQUIRED_MATCHES
```

After `CompletedMatches >= REQUIRED_MATCHES`, use `ActualAverage` only.

---

### 5.4 Provisional Safeguards

During provisional period:

* Handicap **may decrease immediately** if shooter overperforms
* Handicap **may not increase above provisional baseline**
* Shooter is flagged as `IsProvisional = true`

---

## 6. Match Scoring Flow

### 6.1 Pre-Match

For each shooter:

1. Load historical data
2. Determine provisional status
3. Compute `HandicapPerSeries`
4. Freeze handicap for match

---

### 6.2 Per Series Update

After each completed series:

1. Record raw series score
2. Add `HandicapPerSeries`
3. Update cumulative totals
4. Recalculate rankings

---

### 6.3 Final Match Result

```
FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
```

---

## 7. Ranking & Display Requirements

### Must Display (Live)

* Shooter name
* Raw cumulative score
* Handicap cumulative score
* Final cumulative score
* Current rank
* Provisional indicator (P)

### Optional Enhancements

* Delta vs average per series
* Separate raw-score leaderboard

---

## 8. Functional Requirements

### Core Requirements

* Handicap must be deterministic
* Results must update after each series
* Handicap must not change mid-match
* System must support manual override (admin)

### Edge Cases

* Shooter with zero history → provisional
* DNFs: ignore incomplete match for averages
* Discipline isolation required

---

## 9. Data Model Suggestions (.NET)

### Shooter

```csharp
class Shooter
{
    Guid Id;
    string Name;
    int Class; // 1, 2, 3
}
```

---

### ShooterStatistics

```csharp
class ShooterStatistics
{
    Guid ShooterId;
    string Discipline;
    int CompletedMatches;
    double AveragePerSeries;
    bool IsProvisional;
}
```

---

### HandicapProfile

```csharp
class HandicapProfile
{
    Guid ShooterId;
    double EffectiveAverage;
    double HandicapPerSeries;
    bool IsProvisional;
}
```

---

### Match

```csharp
class Match
{
    Guid Id;
    DateTime Date;
    int SeriesCount;
    string Discipline;
}
```

---

### SeriesResult

```csharp
class SeriesResult
{
    Guid MatchId;
    Guid ShooterId;
    int SeriesNumber;
    double RawScore;
    double HandicapApplied;
}
```

---

### MatchResult

```csharp
class MatchResult
{
    Guid MatchId;
    Guid ShooterId;
    double RawTotal;
    double HandicapTotal;
    double FinalTotal;
    int Rank;
}
```

---

## 10. Non-Functional Requirements

* All calculations must be reproducible
* Handicap rounding: **quarter-point (0.25) precision**
* Final scores (raw + handicap): rounded to nearest integer
* Full audit trail of handicap calculations
* Configuration values must be externally configurable

---

## 11. Configuration Parameters

```json
{
  "ReferenceSeriesScore": 48.0,
  "MaxHandicapPerSeries": 5.0,
  "RequiredMatches": 8,
  "ProvisionalAverages": {
    "Class1": 40.0,
    "Class2": 44.0,
    "Class3": 47.0
  }
}
```

---

## 12. Summary

This handicap system:

* Enables fair, motivating competition
* Supports live per-series rankings
* Integrates cleanly with electronic targets
* Uses quarter-point (0.25) precision for handicap values
* Is deterministic, transparent, and SPSF-compatible
* Is well-suited for implementation in a .NET application
