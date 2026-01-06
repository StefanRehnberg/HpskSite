# Finals Competition System - Quick Start Guide

## 🚀 **5-Minute Setup**

### Step 1: Configure Competition (Umbraco Backoffice)
1. Go to Content → Competitions → Your Competition
2. Set properties:
   - **Number Of Series Or Stations:** `10` (7 qual + 3 finals)
   - **Number Of Final Series:** `3`
3. Save and publish

**Result:** System now knows this is a championship.

---

### Step 2: Generate Qualification Start List
1. Go to Competition Management page (`/competitionmanagement?competitionId=XXXX`)
2. Click **Start Lists** tab
3. Configure settings (team format, max shooters, etc.)
4. Click **Generate New Start List**
5. Mark as **Official** ✅

**Result:** Qualification start list ready for 7 series.

---

### Step 3: Enter Qualification Results
1. Click **Results** tab
2. Phase selector shows: **Qualification (Series 1-7)**
3. Select team, position, series
4. Enter shots using keypad
5. Repeat for all shooters, all 7 series

**Result:** Qualification round complete.

---

### Step 4: Generate Finals Start List
1. Go back to **Start Lists** tab
2. Scroll to **🏆 Finals Start List** section
3. System automatically checks qualification status
4. Review qualification summary (qualifiers per class)
5. Click **Generera Finalstartlista**

**Result:** Finals start list created with qualified shooters.

---

### Step 5: Enter Finals Results
1. Go to **Results** tab
2. Click **Finals (Series F1-F3)** radio button
3. System loads finals start list (Team F1, F2, etc.)
4. Series dropdown shows: **Finals 1 (F1)**, **Finals 2 (F2)**, **Finals 3 (F3)**
5. Enter shots for finals series

**Result:** Finals results recorded.

---

### Step 6: View Final Results
1. Go to public competition page
2. Click **Results** button
3. See results table with:
   - Qualification series (1-7) + Qual Total
   - Finals series (F1-F3)
   - Grand Total
   - X count

**Result:** Complete championship results displayed! 🎉

---

## 📋 **Qualification Rules**

### Automatic Calculation:
- **Rule:** Best 1/6 of shooters qualify (rounded up)
- **Minimum:** At least 10 shooters always qualify
- **Exception:** If less than 10 in class, all advance
- **Ties:** Shooter with more X's advances. If tied, count-back from last series.

### Championship Classes:
- **A Class:** All A1, A2, A3 shooters compete together
- **B Class:** All B1, B2, B3 shooters compete together
- **C Class:** All C1, C2, C3 shooters compete together
- **C Dam:** C1 Dam, C2 Dam, C3 Dam
- **C Jun:** C Jun
- **C Vet Y:** C Vet Y
- **C Vet Ä:** C Vet Ä

---

## 🎯 **Finals Team Structure**

### Default Rules:
1. **A Class** gets own teams
2. **B Class** gets own teams
3. **C Classes** combined into shared teams (preserving class groupings)

### Team Naming:
- Qualification: Team 1, Team 2, Team 3...
- Finals: Team F1, Team F2, Team F3...

---

## 🔧 **Troubleshooting**

### Issue: Finals section not showing
**Fix:** Verify `numberOfFinalSeries > 0` in Competition properties.

### Issue: "No qualification results"
**Fix:** Enter at least one result for series 1-7.

### Issue: "Finals start list not found"
**Fix:** Generate it first from Start Lists tab.

### Issue: Wrong number of qualifiers
**Fix:** Check that 1/6 rule is applied (min 10). System calculates automatically.

---

## 📞 **API Endpoints**

### For Developers:

```javascript
// Check qualification status
GET /umbraco/surface/StartList/CalculateFinalsQualifiers?competitionId=2171

// Generate finals start list
POST /umbraco/surface/StartList/GenerateFinalsStartList
Body: { "CompetitionId": 2171, "MaxShootersPerTeam": 20 }

// Get finals start list
GET /umbraco/surface/StartList/GetFinalsStartList?competitionId=2171
```

---

## 🎉 **You're Done!**

The system handles all the complexity:
- ✅ Qualification calculation (1/6 rule)
- ✅ Finals team generation
- ✅ Results entry phase switching
- ✅ Tie-breaking prioritization
- ✅ Results display with qual + finals

**Just follow the 6 steps above and you're set!** 🚀

---

*For full technical details, see `FINALS_IMPLEMENTATION_COMPLETE.md`*





