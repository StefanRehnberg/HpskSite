# Finals Competition System - Testing Guide

## 🧪 **Complete Test Workflow**

This guide walks you through testing the entire finals system from start to finish.

---

## 📋 **Pre-Test Checklist**

Before starting, ensure:
- [ ] Code compiled without errors
- [ ] Application is running
- [ ] You have admin access to Umbraco backoffice
- [ ] Test competition exists (e.g., ID 2171 "1a Maj Skjutningen")
- [ ] Test competition has member registrations

---

## 🎯 **Test Scenario 1: Championship Setup**

### Objective: Configure a competition as a championship

### Steps:
1. **Navigate to Competition**
   - Go to Umbraco backoffice
   - Content → Competitions → 2026 → "1a Maj Skjutningen"

2. **Set Finals Configuration**
   - Find property: **Number Of Series Or Stations**
   - Set to: `10`
   - Find property: **Number Of Final Series**
   - Set to: `3`
   - Click **Save and publish**

3. **Verify**
   - Check console logs (if any)
   - System should now treat this as a championship

### Expected Result:
✅ Competition configured with 7 qualification series + 3 finals series

### Screenshot Locations:
- Umbraco backoffice: Competition properties
- `numberOfSeriesOrStations = 10`
- `numberOfFinalSeries = 3`

---

## 🎯 **Test Scenario 2: Generate Qualification Start List**

### Objective: Create the official start list for qualification round

### Steps:
1. **Navigate to Management Page**
   - Go to: `/competitionmanagement?competitionId=2171`

2. **Open Start Lists Tab**
   - Click **Startlistor** button/tab

3. **Configure Settings**
   - Team Format: `En vapengrupp per Skjutlag`
   - Max Shooters per Team: `30`
   - First Start Time: `09:00`
   - Start Interval: `105` minutes

4. **Generate List**
   - Click **Generate New Start List**
   - Confirm in dialog
   - Wait for success message

5. **Mark as Official**
   - Find the newly created start list in table
   - Click "Mark as Official" button
   - Confirm

### Expected Result:
✅ Qualification start list generated and marked official  
✅ Shows correct number of teams and shooters  
✅ Can preview the start list

### Verify:
- [ ] Success message appears
- [ ] Start list shows in table
- [ ] Status = "Official"
- [ ] Preview button works

---

## 🎯 **Test Scenario 3: Enter Qualification Results**

### Objective: Enter results for all 7 qualification series

### Steps:
1. **Navigate to Results Tab**
   - Stay on management page
   - Click **Resultat** button/tab

2. **Verify Phase Selector**
   - **IMPORTANT:** Check if phase selector appears
   - Should show: **Qualification (Series 1-7)** and **Finals (Series F1-F3)**
   - **Qualification** should be selected by default

3. **Expand Results Entry**
   - Click "Registrera Resultat" header if collapsed

4. **Select First Shooter**
   - Team: Select `1`
   - Position: Select `1`
   - Series: Select `Series 1`
   - Verify shooter name appears

5. **Enter Shots**
   - Use keypad or keyboard
   - Enter 5 shot values (e.g., 10, 10, 9, 9, 8)
   - Press Enter
   - Verify shots saved

6. **Continue Entry**
   - Use "Next Shooter" or "Next Series" mode
   - Enter results for at least:
     - 20+ shooters in A class
     - 15+ shooters in B class
     - 25+ shooters in C class
   - Complete all 7 series for these shooters

### Expected Result:
✅ Qualification results entered for series 1-7  
✅ Results saved to database  
✅ Can see results in results list section

### Verify:
- [ ] Shots save successfully
- [ ] Auto-navigation works (Next Shooter/Series)
- [ ] Can see entered results in "Resultatlista" section
- [ ] Series dropdown only shows 1-7 (not finals yet)

### Time Estimate: 15-30 minutes

---

## 🎯 **Test Scenario 4: Check Qualification Status**

### Objective: Verify system calculates qualifiers correctly

### Steps:
1. **Navigate to Start Lists Tab**
   - Click **Startlistor** tab

2. **Scroll to Finals Section**
   - Look for **🏆 Finals Start List** card
   - Should appear automatically for championships

3. **Wait for Calculation**
   - System auto-checks qualification status
   - Should show loading spinner briefly

4. **Review Qualification Summary**
   - Should show table with:
     - Championship classes (A, B, C, etc.)
     - Total shooters in each class
     - Number of qualifiers
     - Qualification rule applied
     - Cutoff score

5. **Verify Calculations**
   - Check A class: ~20 shooters → 10 qualifiers (1/6 ≈ 3, but min 10)
   - Check B class: ~15 shooters → 10 qualifiers (1/6 ≈ 2.5, but min 10)
   - Check C class: ~25 shooters → 10 qualifiers (1/6 ≈ 4, but min 10)

### Expected Result:
✅ Qualification status calculated automatically  
✅ Summary table shows correct qualifiers  
✅ 1/6 rule applied (minimum 10)  
✅ "Generate Finals Start List" button enabled

### Verify:
- [ ] Finals section visible
- [ ] Qualification summary displays
- [ ] Numbers make sense (1/6 rule, min 10)
- [ ] Cutoff scores shown
- [ ] Generate button enabled

---

## 🎯 **Test Scenario 5: Generate Finals Start List**

### Objective: Create the finals start list from qualifiers

### Steps:
1. **Review Qualification Summary**
   - Note total number of qualifiers (e.g., 30)

2. **Set Max Shooters per Team**
   - Input: `20` (default)
   - This will create 2 finals teams

3. **Click Generate Button**
   - Click **Generera Finalstartlista**
   - Confirm in dialog

4. **Wait for Success**
   - Should see success message
   - Message shows:
     - Number of finalists
     - Number of finals teams

5. **View Finals Start List**
   - Click **Visa Finalstartlista** button
   - Opens in new tab
   - Review finals teams

### Expected Result:
✅ Finals start list generated successfully  
✅ Correct number of finalists  
✅ Teams organized correctly (A, B separate; C combined)  
✅ Can preview finals start list

### Verify:
- [ ] Success message appears
- [ ] Total finalists = sum of qualifiers
- [ ] Teams = ceil(finalists / 20)
- [ ] Preview shows Team F1, Team F2, etc.
- [ ] Shooters show qualification rank and score
- [ ] A and B classes in separate teams
- [ ] C classes combined appropriately

---

## 🎯 **Test Scenario 6: Switch to Finals Phase**

### Objective: Switch results entry to finals mode

### Steps:
1. **Navigate to Results Tab**
   - Click **Resultat** tab

2. **Verify Phase Selector**
   - Should show two options:
     - Qualification (Series 1-7)
     - **Finals (Series F1-F3)**

3. **Click Finals Radio Button**
   - Click **Finals (Series F1-F3)**
   - Wait for system to switch

4. **Verify UI Updates**
   - Description changes to "Enter finals results (series F1-F3)"
   - Team dropdown updates to show "Team F1", "Team F2"
   - Series dropdown shows "Finals 1 (F1)", "Finals 2 (F2)", "Finals 3 (F3)"
   - Position dropdown shows finalist names

5. **Check Console Logs**
   - Open browser console (F12)
   - Look for:
     - "Phase changed to: finals"
     - "Loading finals start list"
     - "Finals start list loaded successfully"

### Expected Result:
✅ Phase switches to finals mode  
✅ Finals start list loads  
✅ UI updates to show finals teams/series  
✅ No JavaScript errors

### Verify:
- [ ] Phase selector switches correctly
- [ ] Description updates
- [ ] Team dropdown shows "Team F1", "Team F2", etc.
- [ ] Series dropdown shows "Finals 1 (F1)", "Finals 2 (F2)", "Finals 3 (F3)"
- [ ] Positions show correct finalists
- [ ] No console errors

---

## 🎯 **Test Scenario 7: Enter Finals Results**

### Objective: Enter results for finals series

### Steps:
1. **Select First Finals Shooter**
   - **Ensure Finals phase is selected**
   - Team: `F1`
   - Position: `1`
   - Series: `Finals 1 (F1)`

2. **Enter Finals Shots**
   - Enter 5 shot values (e.g., 10, X, 10, 9, 10)
   - Press Enter
   - Verify shots saved

3. **Continue for All Finalists**
   - Enter results for all positions in Team F1
   - Switch to Team F2
   - Enter results for Team F2
   - Repeat for all 3 finals series

4. **Verify Series Numbers**
   - Open browser console
   - When saving, check the series number in the request
   - Should be: 8, 9, 10 (not F1, F2, F3)
   - System automatically maps F1→8, F2→9, F3→10

### Expected Result:
✅ Finals results entered successfully  
✅ Series stored as 8, 9, 10 in database  
✅ All finalists have 3 finals series

### Verify:
- [ ] Can select finals teams
- [ ] Can select finals series
- [ ] Shots save successfully
- [ ] Database stores series as 8, 9, 10
- [ ] No errors in console

### Time Estimate: 10-15 minutes

---

## 🎯 **Test Scenario 8: View Combined Results**

### Objective: Verify results display shows qual + finals

### Steps:
1. **Navigate to Results List**
   - Scroll down to **Resultatlista** section
   - Should auto-load if results exist

2. **Verify Table Structure**
   - Check table header:
     - Columns: `#`, `Name`, `Club`, `1`, `2`, `3`, `4`, `5`, `6`, `7`, **`Tot`**, `F1`, `F2`, `F3`, **`Tot`**, `X`
     - Note: Two "Tot" columns (qual total, grand total)

3. **Check Data Display**
   - Verify qualification scores (series 1-7)
   - Verify qualification total (sum of series 1-7)
   - Verify finals scores (F1, F2, F3)
   - Verify grand total (qual + finals)
   - Verify X count

4. **Test Sorting/Tie-breaking**
   - Find shooters with same grand total
   - Verify shooter with more X's is ranked higher
   - If X's tied, verify finals count-back (last finals series first)

5. **Check Public Page**
   - Navigate to public competition page
   - Click **Results** tab
   - Verify same results display

### Expected Result:
✅ Results table shows all columns correctly  
✅ Qualification total calculated correctly  
✅ Finals scores displayed  
✅ Grand total correct  
✅ Tie-breaking prioritizes finals

### Verify:
- [ ] Table has correct columns
- [ ] Qualification series (1-7) show
- [ ] Qualification total column shows (after 7)
- [ ] Finals series (F1-F3) show
- [ ] Grand total column shows (after F3)
- [ ] X count column shows
- [ ] Sorting is correct
- [ ] Public page matches management page

---

## 🎯 **Test Scenario 9: Switch Back to Qualification**

### Objective: Verify can switch back to qualification phase

### Steps:
1. **Go to Results Tab**
   - Ensure phase selector is visible

2. **Click Qualification Radio Button**
   - Click **Qualification (Series 1-7)**

3. **Verify UI Reverts**
   - Description: "Enter qualification round results (series 1-7)"
   - Team dropdown: "Team 1", "Team 2", etc.
   - Series dropdown: "Series 1", "Series 2", ..., "Series 7"

4. **Try Editing Qualification Results**
   - Select a shooter with existing qual results
   - Modify one of their series
   - Verify saves correctly

### Expected Result:
✅ Can switch back to qualification mode  
✅ UI reverts to qualification state  
✅ Can still edit qualification results

### Verify:
- [ ] Phase switches back
- [ ] Teams show as "Team 1", "Team 2"
- [ ] Series show as 1-7
- [ ] Can load and edit qual results

---

## 🎯 **Test Scenario 10: Edge Cases**

### Objective: Test boundary conditions

### Test 10.1: Regular Competition (No Finals)
1. Create/edit a competition
2. Set `numberOfFinalSeries = 0`
3. Go to management page
4. **Verify:** No finals section appears
5. **Verify:** No phase selector in results
6. **Verify:** Only regular series show

### Test 10.2: Small Class (< 10 Shooters)
1. Use a class with only 5 shooters
2. Enter qualification results
3. Generate finals start list
4. **Verify:** All 5 shooters qualify (not 1/6)
5. **Verify:** Message says "All Advance"

### Test 10.3: Exactly 10 Shooters
1. Use a class with exactly 10 shooters
2. Enter qualification results
3. **Verify:** All 10 qualify (not 2, which is 1/6)

### Test 10.4: Ties at Cutoff
1. Ensure 2+ shooters have same score at cutoff
2. **Verify:** System uses X-count to break tie
3. **Verify:** If X-count tied, uses count-back

### Test 10.5: No Qualification Results
1. Create new championship
2. Don't enter any results
3. Go to finals section
4. **Verify:** Shows "Qualification not complete"
5. **Verify:** Generate button disabled

### Test 10.6: Missing Finals Start List
1. Enter qualification results
2. Skip generating finals start list
3. Try switching to finals phase
4. **Verify:** Alert: "Ingen finalstartlista finns"
5. **Verify:** Auto-switches back to qualification

---

## 📊 **Test Results Checklist**

After completing all scenarios, verify:

### Backend
- [ ] Competition detects as championship
- [ ] Qualification results stored in database (series 1-7)
- [ ] Finals results stored in database (series 8-10)
- [ ] Qualification calculation works (1/6 rule, min 10)
- [ ] Finals start list generated correctly
- [ ] No backend errors in logs

### Frontend - Start Lists
- [ ] Finals section appears for championships
- [ ] Qualification status auto-checks
- [ ] Qualification summary displays
- [ ] Generate button works
- [ ] Finals start list preview works

### Frontend - Results Entry
- [ ] Phase selector appears for championships
- [ ] Can switch between phases
- [ ] Qualification phase loads regular start list
- [ ] Finals phase loads finals start list
- [ ] Series dropdown updates correctly
- [ ] Team/position dropdowns update correctly
- [ ] Can enter results in both phases

### Frontend - Results Display
- [ ] Results table shows qual + finals columns
- [ ] Qualification total column appears
- [ ] Finals series columns appear
- [ ] Grand total calculated correctly
- [ ] X count correct
- [ ] Tie-breaking works (finals priority)
- [ ] Public page matches management page

### Edge Cases
- [ ] Regular competitions work (no finals)
- [ ] Small classes handled (all advance)
- [ ] Ties handled correctly
- [ ] Missing data handled gracefully
- [ ] Error messages clear and helpful

---

## 🐛 **Bug Report Template**

If you find issues, report using this template:

```
**Bug:** [Short description]

**Scenario:** Test Scenario #X

**Steps to Reproduce:**
1. ...
2. ...
3. ...

**Expected Result:**
[What should happen]

**Actual Result:**
[What actually happened]

**Screenshots:**
[Attach if applicable]

**Console Errors:**
[Copy from browser console]

**Environment:**
- Browser: [Chrome/Firefox/Edge]
- Version: [e.g., 120.0]
- OS: [Windows/Mac]
```

---

## ✅ **Success Criteria**

All test scenarios should pass with:
- ✅ No JavaScript errors
- ✅ No backend errors
- ✅ Expected UI behavior
- ✅ Correct data storage
- ✅ Accurate calculations
- ✅ Graceful error handling

---

## 🎉 **Testing Complete!**

Once all scenarios pass, the system is ready for production use!

**Estimated Total Testing Time:** 1-2 hours

---

*For quick reference, see `FINALS_QUICK_START.md`*  
*For technical details, see `FINALS_IMPLEMENTATION_COMPLETE.md`*





