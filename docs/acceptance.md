# First-release acceptance checklist

These are the user's original success criteria. A check requires runtime evidence, not just source code or a successful compile. Keep this list separate from progress on the initial local vertical slice.

- [ ] 1. Run the Windows Storage Service on Windows.
- [ ] 2. Run the Android Storage Service on Android.
- [ ] 3. Pair devices through the graphical interface.
- [ ] 4. Open native Storage Browser on both devices.
- [ ] 5. Drag a file into Windows Browser.
- [ ] 6. Observe it become an Item.
- [ ] 7. Inspect automatically populated Attributes.
- [ ] 8. Observe the Item appear on Android.
- [ ] 9. Open or download its content on Android.
- [ ] 10. Upload from Android and observe it on Windows.
- [ ] 11. Browse and manage Collections through the GUI.
- [ ] 12. Create and execute a saved Query through the GUI.
- [ ] 13. Create an ItemVersion and retrieve the unchanged previous version.
- [ ] 14. Change AccessPolicy and verify current/historical authorization under the approved offline-access model.
- [ ] 15. Pin to a device and read verified content while disconnected.
- [ ] 16. Inspect actual replication and placement state through the GUI.

Initial scope: transactional local shared engine, native binding feasibility and native local import/browse UI. Pairing, distributed metadata, replication, policy planning, saved Queries and production publication are later slices unless implementation evidence explicitly records otherwise. Local repository ownership is not a substitute for tested application authorization.
