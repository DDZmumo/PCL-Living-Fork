// LF Handles/WithEvents, original Roslyn symbols at 6a8bfc6; LF LICENCE. No runtime lookup or generated WPF output mutation.
namespace PCL
{
    partial class PageInstanceResources
    {
        private global::System.Windows.Controls.Grid __LfPart_PanAllBack;
        internal virtual global::System.Windows.Controls.Grid PanAllBack
        {
            get
            {
                return __LfPart_PanAllBack;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanAllBack = value;
            }
        }

        private global::PCL.MyScrollViewer __LfPart_PanBack;
        internal virtual global::PCL.MyScrollViewer PanBack
        {
            get
            {
                return __LfPart_PanBack;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::System.Windows.Controls.ScrollChangedEventHandler hook0 = new global::System.Windows.Controls.ScrollChangedEventHandler(this.__LfHandle_15);
                global::System.Windows.SizeChangedEventHandler hook1 = new global::System.Windows.SizeChangedEventHandler(this.__LfHandle_16);
                global::PCL.MyScrollViewer previous = __LfPart_PanBack;
                if (previous != null)
                {
                    previous.ScrollChanged -= hook0;
                    previous.SizeChanged -= hook1;
                }

                __LfPart_PanBack = value;
                global::PCL.MyScrollViewer current = __LfPart_PanBack;
                if (current != null)
                {
                    current.ScrollChanged += hook0;
                    current.SizeChanged += hook1;
                }
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanMain;
        internal virtual global::System.Windows.Controls.StackPanel PanMain
        {
            get
            {
                return __LfPart_PanMain;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanMain = value;
            }
        }

        private global::PCL.WorldBackupSettings __LfPart_BackupSettings;
        internal virtual global::PCL.WorldBackupSettings BackupSettings
        {
            get
            {
                return __LfPart_BackupSettings;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_BackupSettings = value;
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabBackupStatus;
        internal virtual global::System.Windows.Controls.TextBlock LabBackupStatus
        {
            get
            {
                return __LfPart_LabBackupStatus;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabBackupStatus = value;
            }
        }

        private global::PCL.MySearchBox __LfPart_SearchBox;
        internal virtual global::PCL.MySearchBox SearchBox
        {
            get
            {
                return __LfPart_SearchBox;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MySearchBox.TextChangedEventHandler hook0 = new global::PCL.MySearchBox.TextChangedEventHandler(this.__LfHandle_8);
                global::System.Windows.Input.KeyEventHandler hook1 = new global::System.Windows.Input.KeyEventHandler(this.SearchKeyDown);
                global::PCL.MySearchBox previous = __LfPart_SearchBox;
                if (previous != null)
                {
                    previous.TextChanged -= hook0;
                    previous.PreviewKeyDown -= hook1;
                }

                __LfPart_SearchBox = value;
                global::PCL.MySearchBox current = __LfPart_SearchBox;
                if (current != null)
                {
                    current.TextChanged += hook0;
                    current.PreviewKeyDown += hook1;
                }
            }
        }

        private global::PCL.MyCard __LfPart_CardManage;
        internal virtual global::PCL.MyCard CardManage
        {
            get
            {
                return __LfPart_CardManage;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_CardManage = value;
            }
        }

        private global::System.Windows.Controls.Grid __LfPart_PanManageButtons;
        internal virtual global::System.Windows.Controls.Grid PanManageButtons
        {
            get
            {
                return __LfPart_PanManageButtons;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::System.Windows.SizeChangedEventHandler hook0 = new global::System.Windows.SizeChangedEventHandler(this.__LfHandle_5);
                global::System.Windows.Controls.Grid previous = __LfPart_PanManageButtons;
                if (previous != null)
                {
                    previous.SizeChanged -= hook0;
                }

                __LfPart_PanManageButtons = value;
                global::System.Windows.Controls.Grid current = __LfPart_PanManageButtons;
                if (current != null)
                {
                    current.SizeChanged += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnOpenFolder;
        internal virtual global::PCL.MyButton BtnOpenFolder
        {
            get
            {
                return __LfPart_BtnOpenFolder;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_17);
                global::PCL.MyButton previous = __LfPart_BtnOpenFolder;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnOpenFolder = value;
                global::PCL.MyButton current = __LfPart_BtnOpenFolder;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnImportFile;
        internal virtual global::PCL.MyButton BtnImportFile
        {
            get
            {
                return __LfPart_BtnImportFile;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_20);
                global::PCL.MyButton previous = __LfPart_BtnImportFile;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnImportFile = value;
                global::PCL.MyButton current = __LfPart_BtnImportFile;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnDownload;
        internal virtual global::PCL.MyButton BtnDownload
        {
            get
            {
                return __LfPart_BtnDownload;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_24);
                global::PCL.MyButton previous = __LfPart_BtnDownload;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnDownload = value;
                global::PCL.MyButton current = __LfPart_BtnDownload;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnSelectAll;
        internal virtual global::PCL.MyButton BtnSelectAll
        {
            get
            {
                return __LfPart_BtnSelectAll;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_13);
                global::PCL.MyButton previous = __LfPart_BtnSelectAll;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnSelectAll = value;
                global::PCL.MyButton current = __LfPart_BtnSelectAll;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnBackupAll;
        internal virtual global::PCL.MyButton BtnBackupAll
        {
            get
            {
                return __LfPart_BtnBackupAll;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_0);
                global::PCL.MyButton previous = __LfPart_BtnBackupAll;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnBackupAll = value;
                global::PCL.MyButton current = __LfPart_BtnBackupAll;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanOperations;
        internal virtual global::System.Windows.Controls.StackPanel PanOperations
        {
            get
            {
                return __LfPart_PanOperations;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanOperations = value;
            }
        }

        private global::PCL.MyTextButton __LfPart_BtnUp;
        internal virtual global::PCL.MyTextButton BtnUp
        {
            get
            {
                return __LfPart_BtnUp;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyTextButton.ClickEventHandler hook0 = new global::PCL.MyTextButton.ClickEventHandler(this.__LfHandle_19);
                global::PCL.MyTextButton previous = __LfPart_BtnUp;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnUp = value;
                global::PCL.MyTextButton current = __LfPart_BtnUp;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabOperation;
        internal virtual global::System.Windows.Controls.TextBlock LabOperation
        {
            get
            {
                return __LfPart_LabOperation;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabOperation = value;
            }
        }

        private global::PCL.MyButton __LfPart_BtnCancel;
        internal virtual global::PCL.MyButton BtnCancel
        {
            get
            {
                return __LfPart_BtnCancel;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_27);
                global::PCL.MyButton previous = __LfPart_BtnCancel;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnCancel = value;
                global::PCL.MyButton current = __LfPart_BtnCancel;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyCard __LfPart_CardList;
        internal virtual global::PCL.MyCard CardList
        {
            get
            {
                return __LfPart_CardList;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_CardList = value;
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanFilter;
        internal virtual global::System.Windows.Controls.StackPanel PanFilter
        {
            get
            {
                return __LfPart_PanFilter;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanFilter = value;
            }
        }

        private global::PCL.MyRadioButton __LfPart_BtnFilterAll;
        internal virtual global::PCL.MyRadioButton BtnFilterAll
        {
            get
            {
                return __LfPart_BtnFilterAll;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyRadioButton.CheckEventHandler hook0 = new global::PCL.MyRadioButton.CheckEventHandler(this.__LfHandle_9);
                global::PCL.MyRadioButton previous = __LfPart_BtnFilterAll;
                if (previous != null)
                {
                    previous.Check -= hook0;
                }

                __LfPart_BtnFilterAll = value;
                global::PCL.MyRadioButton current = __LfPart_BtnFilterAll;
                if (current != null)
                {
                    current.Check += hook0;
                }
            }
        }

        private global::PCL.MyRadioButton __LfPart_BtnFilterUpdates;
        internal virtual global::PCL.MyRadioButton BtnFilterUpdates
        {
            get
            {
                return __LfPart_BtnFilterUpdates;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyRadioButton.CheckEventHandler hook0 = new global::PCL.MyRadioButton.CheckEventHandler(this.__LfHandle_10);
                global::PCL.MyRadioButton previous = __LfPart_BtnFilterUpdates;
                if (previous != null)
                {
                    previous.Check -= hook0;
                }

                __LfPart_BtnFilterUpdates = value;
                global::PCL.MyRadioButton current = __LfPart_BtnFilterUpdates;
                if (current != null)
                {
                    current.Check += hook0;
                }
            }
        }

        private global::PCL.MyRadioButton __LfPart_BtnFilterFiles;
        internal virtual global::PCL.MyRadioButton BtnFilterFiles
        {
            get
            {
                return __LfPart_BtnFilterFiles;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyRadioButton.CheckEventHandler hook0 = new global::PCL.MyRadioButton.CheckEventHandler(this.__LfHandle_11);
                global::PCL.MyRadioButton previous = __LfPart_BtnFilterFiles;
                if (previous != null)
                {
                    previous.Check -= hook0;
                }

                __LfPart_BtnFilterFiles = value;
                global::PCL.MyRadioButton current = __LfPart_BtnFilterFiles;
                if (current != null)
                {
                    current.Check += hook0;
                }
            }
        }

        private global::PCL.MyRadioButton __LfPart_BtnFilterFolders;
        internal virtual global::PCL.MyRadioButton BtnFilterFolders
        {
            get
            {
                return __LfPart_BtnFilterFolders;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyRadioButton.CheckEventHandler hook0 = new global::PCL.MyRadioButton.CheckEventHandler(this.__LfHandle_12);
                global::PCL.MyRadioButton previous = __LfPart_BtnFilterFolders;
                if (previous != null)
                {
                    previous.Check -= hook0;
                }

                __LfPart_BtnFilterFolders = value;
                global::PCL.MyRadioButton current = __LfPart_BtnFilterFolders;
                if (current != null)
                {
                    current.Check += hook0;
                }
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanResourceList;
        internal virtual global::System.Windows.Controls.StackPanel PanResourceList
        {
            get
            {
                return __LfPart_PanResourceList;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanResourceList = value;
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabEmpty;
        internal virtual global::System.Windows.Controls.TextBlock LabEmpty
        {
            get
            {
                return __LfPart_LabEmpty;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabEmpty = value;
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanList;
        internal virtual global::System.Windows.Controls.StackPanel PanList
        {
            get
            {
                return __LfPart_PanList;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanList = value;
            }
        }

        private global::System.Windows.Controls.StackPanel __LfPart_PanWorlds;
        internal virtual global::System.Windows.Controls.StackPanel PanWorlds
        {
            get
            {
                return __LfPart_PanWorlds;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanWorlds = value;
            }
        }

        private global::System.Windows.Controls.Primitives.UniformGrid __LfPart_PanScreenshots;
        internal virtual global::System.Windows.Controls.Primitives.UniformGrid PanScreenshots
        {
            get
            {
                return __LfPart_PanScreenshots;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanScreenshots = value;
            }
        }

        private global::PCL.MyCard __LfPart_PanEmpty;
        internal virtual global::PCL.MyCard PanEmpty
        {
            get
            {
                return __LfPart_PanEmpty;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanEmpty = value;
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabEmptyTitle;
        internal virtual global::System.Windows.Controls.TextBlock LabEmptyTitle
        {
            get
            {
                return __LfPart_LabEmptyTitle;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabEmptyTitle = value;
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabEmptyHint;
        internal virtual global::System.Windows.Controls.TextBlock LabEmptyHint
        {
            get
            {
                return __LfPart_LabEmptyHint;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabEmptyHint = value;
            }
        }

        private global::PCL.MyButton __LfPart_BtnHintDownload;
        internal virtual global::PCL.MyButton BtnHintDownload
        {
            get
            {
                return __LfPart_BtnHintDownload;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_25);
                global::PCL.MyButton previous = __LfPart_BtnHintDownload;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnHintDownload = value;
                global::PCL.MyButton current = __LfPart_BtnHintDownload;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnHintInstall;
        internal virtual global::PCL.MyButton BtnHintInstall
        {
            get
            {
                return __LfPart_BtnHintInstall;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_21);
                global::PCL.MyButton previous = __LfPart_BtnHintInstall;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnHintInstall = value;
                global::PCL.MyButton current = __LfPart_BtnHintInstall;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyButton __LfPart_BtnHintOpen;
        internal virtual global::PCL.MyButton BtnHintOpen
        {
            get
            {
                return __LfPart_BtnHintOpen;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyButton.ClickEventHandler hook0 = new global::PCL.MyButton.ClickEventHandler(this.__LfHandle_18);
                global::PCL.MyButton previous = __LfPart_BtnHintOpen;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnHintOpen = value;
                global::PCL.MyButton current = __LfPart_BtnHintOpen;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyCard __LfPart_PanLoad;
        internal virtual global::PCL.MyCard PanLoad
        {
            get
            {
                return __LfPart_PanLoad;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_PanLoad = value;
            }
        }

        private global::PCL.MyLoading __LfPart_Load;
        internal virtual global::PCL.MyLoading Load
        {
            get
            {
                return __LfPart_Load;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyLoading.ClickEventHandler hook0 = new global::PCL.MyLoading.ClickEventHandler(this.__LfHandle_28);
                global::PCL.MyLoading previous = __LfPart_Load;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_Load = value;
                global::PCL.MyLoading current = __LfPart_Load;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyCard __LfPart_CardSelect;
        internal virtual global::PCL.MyCard CardSelect
        {
            get
            {
                return __LfPart_CardSelect;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_CardSelect = value;
            }
        }

        private global::System.Windows.Media.TranslateTransform __LfPart_TransSelect;
        internal virtual global::System.Windows.Media.TranslateTransform TransSelect
        {
            get
            {
                return __LfPart_TransSelect;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_TransSelect = value;
            }
        }

        private global::System.Windows.Controls.TextBlock __LfPart_LabSelect;
        internal virtual global::System.Windows.Controls.TextBlock LabSelect
        {
            get
            {
                return __LfPart_LabSelect;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                __LfPart_LabSelect = value;
            }
        }

        private global::PCL.MyIconTextButton __LfPart_BtnUpdate;
        internal virtual global::PCL.MyIconTextButton BtnUpdate
        {
            get
            {
                return __LfPart_BtnUpdate;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyIconTextButton.ClickEventHandler hook0 = new global::PCL.MyIconTextButton.ClickEventHandler(this.__LfHandle_26);
                global::PCL.MyIconTextButton previous = __LfPart_BtnUpdate;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnUpdate = value;
                global::PCL.MyIconTextButton current = __LfPart_BtnUpdate;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyIconTextButton __LfPart_BtnBackupRestore;
        internal virtual global::PCL.MyIconTextButton BtnBackupRestore
        {
            get
            {
                return __LfPart_BtnBackupRestore;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyIconTextButton.ClickEventHandler hook0 = new global::PCL.MyIconTextButton.ClickEventHandler(this.__LfHandle_1);
                global::PCL.MyIconTextButton previous = __LfPart_BtnBackupRestore;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnBackupRestore = value;
                global::PCL.MyIconTextButton current = __LfPart_BtnBackupRestore;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyIconTextButton __LfPart_BtnExport;
        internal virtual global::PCL.MyIconTextButton BtnExport
        {
            get
            {
                return __LfPart_BtnExport;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyIconTextButton.ClickEventHandler hook0 = new global::PCL.MyIconTextButton.ClickEventHandler(this.__LfHandle_22);
                global::PCL.MyIconTextButton previous = __LfPart_BtnExport;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnExport = value;
                global::PCL.MyIconTextButton current = __LfPart_BtnExport;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyIconTextButton __LfPart_BtnDelete;
        internal virtual global::PCL.MyIconTextButton BtnDelete
        {
            get
            {
                return __LfPart_BtnDelete;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyIconTextButton.ClickEventHandler hook0 = new global::PCL.MyIconTextButton.ClickEventHandler(this.__LfHandle_23);
                global::PCL.MyIconTextButton previous = __LfPart_BtnDelete;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnDelete = value;
                global::PCL.MyIconTextButton current = __LfPart_BtnDelete;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private global::PCL.MyIconTextButton __LfPart_BtnDeselect;
        internal virtual global::PCL.MyIconTextButton BtnDeselect
        {
            get
            {
                return __LfPart_BtnDeselect;
            }

            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
            set
            {
                global::PCL.MyIconTextButton.ClickEventHandler hook0 = new global::PCL.MyIconTextButton.ClickEventHandler(this.__LfHandle_14);
                global::PCL.MyIconTextButton previous = __LfPart_BtnDeselect;
                if (previous != null)
                {
                    previous.Click -= hook0;
                }

                __LfPart_BtnDeselect = value;
                global::PCL.MyIconTextButton current = __LfPart_BtnDeselect;
                if (current != null)
                {
                    current.Click += hook0;
                }
            }
        }

        private void __LfHandle_0(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.BackupAll();
        }

        private void __LfHandle_1(global::System.Object arg0, global::PCL.ModBase.RouteEventArgs arg1)
        {
            this.RestoreWorldBackup();
        }

        private void __LfHandle_5(global::System.Object arg0, global::System.Windows.SizeChangedEventArgs arg1)
        {
            this.RefreshResourceToolbar();
        }

        private void __LfHandle_6(global::System.Object arg0, global::System.Windows.RoutedEventArgs arg1)
        {
            this.PageLoaded();
        }

        private void __LfHandle_7(global::System.Object arg0, global::System.Windows.RoutedEventArgs arg1)
        {
            this.PageUnloaded();
        }

        private void __LfHandle_8(global::System.Object arg0, global::System.EventArgs arg1)
        {
            this.SearchChanged();
        }

        private void __LfHandle_9(global::System.Object arg0, global::System.Boolean arg1)
        {
            this.FilterChanged((global::PCL.MyRadioButton)arg0, (global::System.Boolean)arg1);
        }

        private void __LfHandle_10(global::System.Object arg0, global::System.Boolean arg1)
        {
            this.FilterChanged((global::PCL.MyRadioButton)arg0, (global::System.Boolean)arg1);
        }

        private void __LfHandle_11(global::System.Object arg0, global::System.Boolean arg1)
        {
            this.FilterChanged((global::PCL.MyRadioButton)arg0, (global::System.Boolean)arg1);
        }

        private void __LfHandle_12(global::System.Object arg0, global::System.Boolean arg1)
        {
            this.FilterChanged((global::PCL.MyRadioButton)arg0, (global::System.Boolean)arg1);
        }

        private void __LfHandle_13(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.SelectAllClick();
        }

        private void __LfHandle_14(global::System.Object arg0, global::PCL.ModBase.RouteEventArgs arg1)
        {
            this.DeselectClick();
        }

        private void __LfHandle_15(global::System.Object arg0, global::System.Windows.Controls.ScrollChangedEventArgs arg1)
        {
            this.RefreshScreenshotViewport();
        }

        private void __LfHandle_16(global::System.Object arg0, global::System.Windows.SizeChangedEventArgs arg1)
        {
            this.RefreshScreenshotViewport();
        }

        private void __LfHandle_17(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.OpenFolder();
        }

        private void __LfHandle_18(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.OpenFolder();
        }

        private void __LfHandle_19(global::System.Object arg0, global::System.EventArgs arg1)
        {
            this.UpClick();
        }

        private void __LfHandle_20(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.ImportFiles();
        }

        private void __LfHandle_21(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.ImportFiles();
        }

        private void __LfHandle_22(global::System.Object arg0, global::PCL.ModBase.RouteEventArgs arg1)
        {
            this.ExportSelected();
        }

        private void __LfHandle_23(global::System.Object arg0, global::PCL.ModBase.RouteEventArgs arg1)
        {
            this.DeleteSelected();
        }

        private void __LfHandle_24(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.DownloadNew();
        }

        private void __LfHandle_25(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.DownloadNew();
        }

        private void __LfHandle_26(global::System.Object arg0, global::PCL.ModBase.RouteEventArgs arg1)
        {
            this.UpdateSelected();
        }

        private void __LfHandle_27(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.CancelClick();
        }

        private void __LfHandle_28(global::System.Object arg0, global::System.Windows.Input.MouseButtonEventArgs arg1)
        {
            this.RetryLoad();
        }

        private void __LfAttachOwnHandles()
        {
            this.Loaded += new global::System.Windows.RoutedEventHandler(this.__LfHandle_6);
            this.Unloaded += new global::System.Windows.RoutedEventHandler(this.__LfHandle_7);
            this.PageExit += new global::PCL.MyPageRight.PageExitEventHandler(this.ClearSelection);
            this.KeyDown += new global::System.Windows.Input.KeyEventHandler(this.ResourceKeyDown);
            this.PreviewDragOver += new global::System.Windows.DragEventHandler(this.ResourceDragOver);
            this.PreviewDrop += new global::System.Windows.DragEventHandler(this.ResourceDrop);
        }
    }
}
