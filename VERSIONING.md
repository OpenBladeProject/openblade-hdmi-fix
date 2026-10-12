# Versioning

`Version` in `Directory.Build.props` is the product-version source of truth. This initial implementation starts at `0.1.0` and remains experimental until physical validation is recorded.

Pull requests state `Version impact`, `Version change`, and `Reason`. Use a patch increment for a compatible fix, minor for a new supported capability or user-facing feature, and major for an incompatible change after 1.0. Recalculate from current main before marking ready or merging. Profile and backup formats have independent schema versions.

Use purpose-prefixed branches and Conventional Commits. Changelog entries link the implementing pull request.
