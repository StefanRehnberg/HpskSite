# Competition Configuration Guide

## 🎯 **Understanding Competition Properties**

### Key Properties

#### `numberOfSeriesOrStations`
**Definition:** The **TOTAL** number of series in the competition (qualification + finals combined).

**Examples:**
- Regular competition with 6 series: `numberOfSeriesOrStations = 6`
- Championship with 7 qualification + 3 finals: `numberOfSeriesOrStations = 10`
- Championship with 10 qualification + 2 finals: `numberOfSeriesOrStations = 12`

#### `numberOfFinalSeries`
**Definition:** The number of finals series (only for championships).

**Examples:**
- Regular competition (no finals): `numberOfFinalSeries = 0`
- Championship with 3 finals series: `numberOfFinalSeries = 3`
- Championship with 2 finals series: `numberOfFinalSeries = 2`

---

## 📐 **Calculation Logic**

The system automatically calculates:

```
qualificationSeriesCount = numberOfSeriesOrStations - numberOfFinalSeries
```

**Example 1: Standard Championship (7+3)**
- `numberOfSeriesOrStations = 10` (total)
- `numberOfFinalSeries = 3`
- **Result:** qualificationSeriesCount = 10 - 3 = **7** ✅

**Example 2: Regular Competition**
- `numberOfSeriesOrStations = 6`
- `numberOfFinalSeries = 0`
- **Result:** qualificationSeriesCount = 6 - 0 = **6** ✅

---

## ⚠️ **Common Configuration Errors**

### Error 1: Setting Total to Qualification Count Only

**❌ WRONG:**
```
numberOfSeriesOrStations = 7  (only qualification)
numberOfFinalSeries = 3
Result: qualificationSeriesCount = 4  ❌ INCORRECT!
```

**✅ CORRECT:**
```
numberOfSeriesOrStations = 10  (qualification + finals)
numberOfFinalSeries = 3
Result: qualificationSeriesCount = 7  ✅ CORRECT!
```

### Error 2: Forgetting to Account for Finals

**Scenario:** You want 7 qualification series and 3 finals series.

**❌ WRONG:**
- Set `numberOfSeriesOrStations = 7`
- Set `numberOfFinalSeries = 3`
- **Problem:** System thinks you have 4 qualification + 3 finals!

**✅ CORRECT:**
- Set `numberOfSeriesOrStations = 10` (7 + 3)
- Set `numberOfFinalSeries = 3`
- **Result:** System correctly identifies 7 qualification + 3 finals!

---

## 🔧 **How to Configure in Umbraco**

### For a Regular Competition (No Finals):
1. Go to Content → Competitions → Your Competition
2. Set **Number Of Series Or Stations** = `6` (or however many series)
3. Set **Number Of Final Series** = `0`
4. Save and publish

### For a Championship (With Finals):
1. Go to Content → Competitions → Your Competition
2. Decide on structure, e.g., 7 qualification + 3 finals
3. Set **Number Of Series Or Stations** = `10` (7 + 3 = total)
4. Set **Number Of Final Series** = `3`
5. Save and publish

---

## ✅ **Validation**

The system will now show an error alert if the configuration is invalid:

**Alert Message:**
```
⚠️ Competition Configuration Error!

Number of series is incorrectly configured.

Current: 7 total series, 3 finals
This gives only 4 qualification series!

Please update the competition:
• numberOfSeriesOrStations should be TOTAL (qual + finals)
• Example: 7 qualification + 3 finals = 10 total
```

If you see this alert, go to Umbraco and update the competition properties.

---

## 📊 **Quick Reference Table**

| Competition Type | Qual Series | Finals | Total (numberOfSeriesOrStations) | Finals (numberOfFinalSeries) |
|------------------|-------------|--------|----------------------------------|------------------------------|
| Regular | 6 | 0 | **6** | **0** |
| Regular | 10 | 0 | **10** | **0** |
| Championship | 7 | 3 | **10** | **3** |
| Championship | 10 | 2 | **12** | **2** |
| Championship | 6 | 4 | **10** | **4** |

---

## 🐛 **Troubleshooting**

### Issue: Phase selector shows wrong series counts
**Example:** Shows "Qualification (Series 1-4)" instead of "1-7"

**Cause:** `numberOfSeriesOrStations` is set to 7 instead of 10.

**Fix:**
1. Check competition properties in Umbraco
2. Verify: `numberOfSeriesOrStations = qualificationSeriesCount + numberOfFinalSeries`
3. Update to correct total
4. Save and publish
5. Refresh management page

### Issue: Console shows "qualificationSeriesCount: 4" but expecting 7

**Diagnosis:**
- Log shows: `numberOfSeries: 7, numberOfFinalSeries: 3, qualificationSeriesCount: 4`
- **Problem:** 7 - 3 = 4 (not enough!)
- **Solution:** Change numberOfSeriesOrStations to 10

**Steps:**
1. Open Umbraco backoffice
2. Navigate to the competition
3. Change `numberOfSeriesOrStations` from 7 to 10
4. Save and publish
5. Refresh browser

---

## 💡 **Remember:**

**Golden Rule:**
```
numberOfSeriesOrStations = qualification series + final series
```

**NOT:**
```
numberOfSeriesOrStations = qualification series only  ❌
```

---

*This guide applies to the Finals Competition System v1.0.0*





